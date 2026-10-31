using ContactManage.Adapters.CardDav;
using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class CardDavStoreTests
{
    private const string Url = "https://dav.example.com/dav/";
    private const string Contacts = FakeDavServer.Home + "Contacts/";

    private static ContactFolder Folder(string path = "/0/Contacts", string name = "Contacts") =>
        new("acct1:" + path, name, path, "Jane", "dav.example.com", FolderKind.CardDav, true, ServerUrl: Url);

    private static string Card(string uid, string name, string? email = null) =>
        $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:{name}\r\nN:;{name};;;\r\n{(email is null ? "" : $"EMAIL;TYPE=WORK:{email}\r\n")}END:VCARD\r\n";

    private static (CardDavContactStore Store, List<string?> Errors) StoreFor(FakeDavServer server, string password = FakeDavServer.Password)
    {
        var errors = new List<string?>();
        var store = new CardDavContactStore((_, error) =>
        {
            errors.Add(error);
            return Task.FromResult<ServerCredentials?>(new ServerCredentials(FakeDavServer.User, password));
        }, server);
        return (store, errors);
    }

    private static FakeDavServer ServerWith(params (string name, string card)[] contacts)
    {
        var s = new FakeDavServer();
        foreach (var (name, card) in contacts) s.Add(Contacts, name, card);
        return s;
    }

    [Fact]
    public async Task Reads_contacts_of_a_folder_by_its_cache_path()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee", "ann@x.example")), ("b.vcf", Card("u2", "Bob Ray")));
        var (store, _) = StoreFor(server);

        var contacts = await store.ReadContactsAsync(Folder());

        Assert.Equal(["Ann Lee", "Bob Ray"], contacts.Select(c => c.DisplayName).Order());
        var ann = contacts.Single(c => c.DisplayName == "Ann Lee");
        Assert.Equal("acct1:/0/Contacts", ann.SourceFolderId);
        Assert.Equal(Contacts + "a.vcf", ann.RemoteId);
        Assert.Equal("u1", ann.Uid);
        Assert.Equal(";TYPE=WORK", ann.Labels["ann@x.example"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_new_folder_is_offered_only_when_the_signed_in_server_lists_MKCOL(bool omitMkcol, bool offered)
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee")));
        server.OmitMkcolFromAllow = omitMkcol;
        var (store, _) = StoreFor(server);
        var folder = Folder();
        Assert.True(store.CanCreateFolder(folder));   // not signed in yet: unknown counts as yes

        await store.ReadContactsAsync(folder);

        Assert.Equal(offered, store.CanCreateFolder(folder));
    }

    [Fact]
    public async Task Lists_the_folder_with_a_plain_listing_when_the_server_refuses_the_query()
    {
        // AOL answers an addressbook-query without a filter with HTTP 400.
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee")), ("b.vcf", Card("u2", "Bob Ray")));
        server.RejectQueries = true;
        var (store, _) = StoreFor(server);

        var contacts = await store.ReadContactsAsync(Folder());

        Assert.Equal(["Ann Lee", "Bob Ray"], contacts.Select(c => c.DisplayName).Order());
    }

    [Fact]
    public async Task Reads_the_real_data_in_batches_not_the_stubs_a_plain_query_returns()
    {
        // IceWarp's addressbook-query returns empty stubs, so a read must go through multiget, 50 contacts at a time.
        var server = ServerWith(Enumerable.Range(1, 120).Select(i => ($"c{i}.vcf", Card($"u{i}", $"Person {i}", $"p{i}@x.example"))).ToArray());
        var (store, _) = StoreFor(server);

        var contacts = await store.ReadContactsAsync(Folder());

        Assert.Equal(120, contacts.Count);
        Assert.All(contacts, c => { Assert.NotNull(c.DisplayName); Assert.Single(c.Emails); });
        Assert.Equal(3, server.Log.Count(l => l == "REPORT " + Contacts) - 1); // one listing, then three batches
    }

    [Fact]
    public async Task Reads_a_nested_shared_folder()
    {
        var server = new FakeDavServer();
        server.Add(FakeDavServer.Home + "Shared/Team/", "t.vcf", Card("t1", "Team Member"));
        var (store, _) = StoreFor(server);

        var contacts = await store.ReadContactsAsync(Folder("/0/Shared/Team", "Team"));

        Assert.Equal("Team Member", Assert.Single(contacts).DisplayName);
    }

    [Fact]
    public async Task Falls_back_to_the_display_name_when_the_path_does_not_match()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee")));
        var (store, _) = StoreFor(server);

        var contacts = await store.ReadContactsAsync(Folder("/9/Something/Else", "Contacts"));

        Assert.Single(contacts);
    }

    [Fact]
    public async Task Unknown_folder_gives_a_clear_error()
    {
        var (store, _) = StoreFor(new FakeDavServer());

        var ex = await Assert.ThrowsAsync<CardDavException>(() => store.ReadContactsAsync(Folder("/0/Nope", "Nope")));

        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public async Task Replace_writes_new_contacts_leaves_untouched_ones_and_deletes_the_rest_last()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee", "ann@x.example")), ("b.vcf", Card("u2", "Ann Lee", "ANN@x.example")), ("c.vcf", Card("u3", "Cy Dee")));
        var (store, _) = StoreFor(server);
        var folder = Folder();
        var read = await store.ReadContactsAsync(folder);
        var merged = new MergeEngine().Merge(read);
        var final = merged.BuildFinalList();
        server.Log.Clear();

        var report = await store.ReplaceContactsAsync(folder, final);

        Assert.Equal(new WriteReport("Contacts", 0, 1, 1, 2, 1), report);   // the merged contact overwrote an original; the other original went
        Assert.True(report.Verified);
        Assert.Equal(2, server.In(Contacts).Count());
        Assert.Contains(Contacts + "c.vcf", server.Resources.Keys);               // untouched contact kept as it was
        Assert.DoesNotContain(server.Log, l => l == $"PUT {Contacts}c.vcf");      // and not rewritten
        var lastPut = server.Log.FindLastIndex(l => l.StartsWith("PUT "));
        var firstDelete = server.Log.FindIndex(l => l.StartsWith("DELETE "));
        Assert.True(lastPut < firstDelete, "all writes must come before any delete");
    }

    [Fact]
    public async Task A_merged_contact_survives_on_a_server_that_treats_the_same_name_and_email_as_one_contact()
    {
        // AOL: writing a merged contact under a new address updates the original with that name and email, so deleting the
        // originals afterwards used to delete the merged contact as well (54 contacts became 46).
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee", "ann@x.example")), ("b.vcf", Card("u2", "Ann Lee", "ann@x.example")), ("c.vcf", Card("u3", "Cy Dee", "cy@x.example")));
        server.FoldSameNameAndEmail = true;
        var (store, _) = StoreFor(server);
        var folder = Folder();
        var final = new MergeEngine().Merge(await store.ReadContactsAsync(folder)).BuildFinalList();

        var report = await store.ReplaceContactsAsync(folder, final);

        Assert.Equal(2, server.In(Contacts).Count());
        Assert.Equal(2, report.CountAfter);
        Assert.True(report.Verified);
        Assert.Contains("FN:Ann Lee", string.Concat(server.Resources.Values));
        Assert.Contains("FN:Cy Dee", string.Concat(server.Resources.Values));
        var survivor = server.Resources.Single(r => r.Value.Contains("FN:Ann Lee"));
        Assert.Contains("UID:u1", survivor.Value);   // kept the UID of the original it overwrote
    }

    [Fact]
    public async Task A_new_contact_the_server_folds_into_an_existing_one_stops_the_replace_before_any_delete()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee", "ann@x.example")), ("c.vcf", Card("u3", "Cy Dee", "cy@x.example")));
        server.FoldSameNameAndEmail = true;
        var (store, _) = StoreFor(server);
        var folder = Folder();
        await store.ReadContactsAsync(folder);
        // Not read from this folder (like a restore from a backup), so it is written as a new entry; it folds into a.vcf.
        var fromBackup = new[] { new Contact { DisplayName = "Ann Lee", Emails = { "ann@x.example" } } };

        var ex = await Assert.ThrowsAsync<CardDavException>(() => store.ReplaceContactsAsync(folder, fromBackup));

        Assert.Contains("Nothing was deleted", ex.Message);
        Assert.Equal(2, server.In(Contacts).Count());
        Assert.DoesNotContain(server.Log, l => l.StartsWith("DELETE "));
    }

    [Fact]
    public async Task Replace_only_lists_the_old_contacts_it_does_not_download_them_and_handles_many_in_parallel()
    {
        var server = ServerWith(Enumerable.Range(1, 120).Select(i => ($"old{i}.vcf", Card($"o{i}", $"Old {i}"))).ToArray());
        var (store, _) = StoreFor(server);
        var fresh = Enumerable.Range(1, 150).Select(i => new Contact { DisplayName = $"New {i}", Emails = { $"n{i}@x.example" } }).ToList();
        var before = server.Log.Count(l => l == "REPORT " + Contacts);

        var report = await store.ReplaceContactsAsync(Folder(), fresh);

        Assert.Equal(2, server.Log.Count(l => l == "REPORT " + Contacts) - before); // a listing before the writes and one to check them, no multiget of 120 vCards
        Assert.Equal(new WriteReport("Contacts", 150, 120, 0, 150), report);
        Assert.Equal(150, server.In(Contacts).Count());
        Assert.DoesNotContain(server.In(Contacts), k => k.Contains("old"));
    }

    [Fact]
    public async Task A_failed_write_in_the_middle_of_a_replace_deletes_nothing()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee")), ("b.vcf", Card("u2", "Bob Ray")));
        var (store, _) = StoreFor(server);
        server.FailPuts = true;

        await Assert.ThrowsAsync<CardDavException>(() => store.ReplaceContactsAsync(Folder(),
            Enumerable.Range(1, 40).Select(i => new Contact { DisplayName = $"New {i}" }).ToList()));

        Assert.Equal(2, server.In(Contacts).Count());
        Assert.DoesNotContain(server.Log, l => l.StartsWith("DELETE"));
    }

    [Fact]
    public async Task Replace_keeps_labels_and_extra_fields_of_merged_contacts_on_the_server()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee", "ann@x.example") .Replace("END:VCARD", "BDAY:1990-01-01\r\nEND:VCARD")),
            ("b.vcf", Card("u2", "Ann Lee", "ann@x.example")));
        var (store, _) = StoreFor(server);
        var folder = Folder();
        var final = new MergeEngine().Merge(await store.ReadContactsAsync(folder)).BuildFinalList();

        await store.ReplaceContactsAsync(folder, final);

        var written = Assert.Single(server.Resources.Values);
        Assert.Contains("EMAIL;TYPE=WORK:ann@x.example", written);
        Assert.Contains("BDAY:1990-01-01", written);
    }

    [Fact]
    public async Task Replace_refuses_to_empty_a_folder()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee")));
        var (store, _) = StoreFor(server);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceContactsAsync(Folder(), []));

        Assert.Single(server.In(Contacts));
    }

    [Fact]
    public async Task A_failed_write_deletes_nothing()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee")), ("b.vcf", Card("u2", "Bob Ray")));
        var (store, _) = StoreFor(server);
        var folder = Folder();
        await store.ReadContactsAsync(folder);
        server.FailPuts = true;

        var ex = await Assert.ThrowsAsync<CardDavException>(() => store.ReplaceContactsAsync(folder, [new Contact { DisplayName = "Fresh" }]));

        Assert.Equal(500, ex.Status);
        Assert.Equal(2, server.In(Contacts).Count());
        Assert.DoesNotContain(server.Log, l => l.StartsWith("DELETE "));
    }

    [Fact]
    public async Task Creates_a_new_folder_and_fills_it()
    {
        var server = new FakeDavServer();
        var (store, _) = StoreFor(server);
        var contacts = new[] { new Contact { DisplayName = "One" }, new Contact { DisplayName = "Two" } };

        var report = await store.CreateFolderAsync(Folder(), "Merged contacts", contacts);

        var book = Assert.Single(server.Books, b => b.Value == "Merged contacts");
        Assert.Equal(2, server.In(book.Key).Count());
        Assert.Equal(new WriteReport("Merged contacts", 2, 0, 0, 2), report);
    }

    [Fact]
    public async Task A_permission_refusal_is_not_reported_as_a_wrong_password()
    {
        var server = new FakeDavServer { ForbidMkcol = true };
        var (store, errors) = StoreFor(server);

        var ex = await Assert.ThrowsAsync<CardDavException>(() =>
            store.CreateFolderAsync(Folder(), "Merged contacts", [new Contact { DisplayName = "One" }]));

        Assert.IsNotType<CardDavAuthException>(ex);
        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("Merged contacts", ex.Message);
        Assert.Single(errors); // no second sign-in prompt
    }

    [Fact]
    public async Task A_new_folder_is_removed_again_when_the_first_write_fails_and_the_servers_reason_is_shown()
    {
        var server = new FakeDavServer { FailPuts = true };
        var (store, _) = StoreFor(server);

        var ex = await Assert.ThrowsAsync<CardDavException>(() =>
            store.CreateFolderAsync(Folder(), "Merged contacts", [new Contact { DisplayName = "One" }]));

        Assert.Contains("HTTP 500", ex.Message);
        Assert.Contains("Server reason: no-uid-conflict", ex.Message);
        Assert.DoesNotContain(server.Books.Values, n => n == "Merged contacts");
    }

    [Fact]
    public async Task New_folder_with_a_taken_name_gets_an_address_of_its_own()
    {
        var server = new FakeDavServer();
        var (store, _) = StoreFor(server);

        await store.CreateFolderAsync(Folder(), "Contacts", [new Contact { DisplayName = "One" }]);

        // The existing "Contacts" is untouched and the new folder lives at an address of its own.
        Assert.Equal("Contacts", server.Books[FakeDavServer.Home + "Contacts/"]);
        Assert.Empty(server.In(FakeDavServer.Home + "Contacts/"));
        var created = Assert.Single(server.Books, b => b.Key != FakeDavServer.Home + "Contacts/" && b.Key.StartsWith(FakeDavServer.Home + "Contacts-") && b.Value == "Contacts");
        Assert.Single(server.In(created.Key));
    }

    [Fact]
    public async Task Wrong_password_asks_again_with_an_error_message()
    {
        var server = ServerWith(("a.vcf", Card("u1", "Ann Lee")));
        var attempts = 0;
        var errors = new List<string?>();
        var store = new CardDavContactStore((_, error) =>
        {
            errors.Add(error);
            return Task.FromResult<ServerCredentials?>(new ServerCredentials(FakeDavServer.User, ++attempts == 1 ? "wrong" : FakeDavServer.Password));
        }, server);

        var contacts = await store.ReadContactsAsync(Folder());

        Assert.Single(contacts);
        Assert.Null(errors[0]);
        Assert.Contains("rejected", errors[1]);
    }

    [Fact]
    public async Task Gives_up_after_three_wrong_passwords()
    {
        var (store, errors) = StoreFor(new FakeDavServer(), "wrong");

        await Assert.ThrowsAsync<CardDavAuthException>(() => store.ReadContactsAsync(Folder()));

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task Cancelling_the_sign_in_cancels_the_operation()
    {
        var store = new CardDavContactStore((_, _) => Task.FromResult<ServerCredentials?>(null), new FakeDavServer());

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadContactsAsync(Folder()));
    }

    [Fact]
    public async Task Refuses_an_unencrypted_address()
    {
        var store = new CardDavContactStore((_, _) => Task.FromResult<ServerCredentials?>(new ServerCredentials("u", "p")), new FakeDavServer());
        var folder = Folder() with { ServerUrl = "http://dav.example.com/dav/" };

        var ex = await Assert.ThrowsAsync<CardDavException>(() => store.ReadContactsAsync(folder));

        Assert.Contains("https", ex.Message);
    }
}
