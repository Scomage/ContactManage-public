using Xunit;
using static ContactManage.UiTests.DemoApp;

namespace ContactManage.UiTests;

/// <summary>The sign-in buttons for online accounts. They are only looked at, never pressed: pressing one would open a browser.</summary>
[Collection("UI")]
[Trait("Category", "UI")]
public class OnlineAccountButtonTests
{
    [Fact]
    public void Both_online_account_buttons_are_there_and_are_switched_off_in_the_demo()
    {
        using var app = DemoApp.Start();   // --demo

        Assert.False(ButtonById(app.Main, "AddGoogleButton").IsEnabled);
        Assert.False(ButtonById(app.Main, "AddMicrosoftButton").IsEnabled);
        Assert.Equal("Add Microsoft account", ButtonById(app.Main, "AddMicrosoftButton").Name);
    }
}
