using System.Text;

/// <summary>Copies console output to a log file so a run in a separate window can be reviewed afterwards. The password is never echoed, so it is never logged.</summary>
sealed class Tee(TextWriter console, TextWriter file) : TextWriter
{
    public override Encoding Encoding => console.Encoding;
    public override void Write(char value) { console.Write(value); file.Write(value); }
    public override void Write(string? value) { console.Write(value); file.Write(value); }
    public override void WriteLine(string? value) { console.WriteLine(value); file.WriteLine(value); }
    public override void Flush() { console.Flush(); file.Flush(); }
}
