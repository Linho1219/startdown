namespace StartDown.WindowFixture;

internal static class Program
{
    public const string WindowTitle = "StartDown Integration Fixture";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var startedMarker = ReadOption(args, "--started-marker");
        var closedMarker = ReadOption(args, "--closed-marker");
        var form = new Form
        {
            Text = WindowTitle,
            ClientSize = new Size(720, 480),
            StartPosition = FormStartPosition.CenterScreen,
        };
        form.Controls.Add(new Label
        {
            Text = "StartDown should close this window.",
            AutoSize = true,
            Location = new Point(24, 24),
        });
        form.Shown += (_, _) => WriteMarker(startedMarker, Environment.ProcessId.ToString());
        form.FormClosed += (_, _) => WriteMarker(closedMarker, "closed");
        Application.Run(form);
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static void WriteMarker(string? path, string value)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllText(path, value);
        }
    }
}
