using System.Reflection;
using System.Text.Json;

namespace GPTBotManager;

internal static class LayoutSmokeTest
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Manager layout: " + message);
    }

    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls) {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }

    public static void Run()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "qqbridge-layout-" + Guid.NewGuid().ToString("N"));
        string[] variables = ["QQB_BRIDGE_DIR", "QQB_DOT_BACKEND_DIR", "QQB_SNOWLUMA_DIR"];
        var environment = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var synchronizationContext = SynchronizationContext.Current;
        try {
            Directory.CreateDirectory(Path.Combine(fixture, "src"));
            File.WriteAllText(Path.Combine(fixture, "src", "bridge.js"), "");
            foreach (string variable in variables) Environment.SetEnvironmentVariable(variable, fixture);

            // Compare equivalent client space at this process's real DPI. This does not
            // change Windows display settings or simulate native per-monitor DPI messages.
            var displays = new List<(int Width, int Height, double Scale)>();
            foreach (var (width, height) in new[] { (1920, 1080), (2560, 1440), (3840, 2160) })
                foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2 }) displays.Add((width, height, scale));
            displays.AddRange([(3840, 2160, 2.5), (3840, 2160, 3), (1366, 768, 1), (1366, 768, 1.25), (1366, 768, 1.5)]);

            foreach (bool dotOnly in new[] { true, false }) {
                File.WriteAllText(Path.Combine(fixture, "config.json"), JsonSerializer.Serialize(new { dot = new { only = dotOnly } }));
                using var form = new ManagerForm();
                // The fixture must never probe real services or start a background task.
                using var timer = (System.Windows.Forms.Timer)typeof(ManagerForm).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                timer.Stop();
                var services = (Service[])typeof(ManagerForm).GetField("services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                foreach (var service in services) service.Status.Text = "运行中 / 等待连接 · PID 999999";
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show();
                Check(form.AutoScaleMode == AutoScaleMode.Dpi, "DPI layout scaling is disabled");
                var controls = Descendants(form).ToArray();
                Check(controls.OfType<Button>().Count() == services.Length * 4 + 5, "An action button is missing");
                var log = controls.OfType<RichTextBox>().Single();
                double dpiScale = form.DeviceDpi / 96d;
                foreach (var (width, height, scale) in displays) {
                    // Reserve 48 logical pixels for the taskbar and test a nonzero screen origin.
                    var area = new Rectangle(40, 50, (int)(width / scale * dpiScale), (int)((height / scale - 48) * dpiScale));
                    form.ClientSize = new Size((int)(1000 * dpiScale), (int)(680 * dpiScale));
                    form.FitToWorkingArea(area);
                    Validate($"{width}x{height} at {scale:P0}, dotOnly={dotOnly}, default");
                    form.Size = form.MinimumSize;
                    Validate($"{width}x{height} at {scale:P0}, dotOnly={dotOnly}, minimum");

                    void Validate(string scenario)
                    {
                        form.PerformLayout();
                        Check(area.Contains(form.Bounds), $"Window exceeds work area: {scenario}");
                        Check(!form.AutoScroll && controls.OfType<Panel>().All(p => !p.AutoScroll), $"Window scrolling enabled: {scenario}");
                        foreach (var control in controls) {
                            Check(control.Parent!.ClientRectangle.Contains(control.Bounds), $"{control.GetType().Name} '{control.Text}' exceeds its parent: {scenario}");
                            if (control is Button or Label or CheckBox) {
                                var preferred = control.GetPreferredSize(new Size(control.Width, 0));
                                Check(control.Width >= preferred.Width && control.Height >= preferred.Height,
                                    $"Text clipped in '{control.Text}': {scenario}");
                            }
                        }
                        Check(log.Height >= log.Font.Height * 3, $"Less than three lines of log space: {scenario}");
                    }
                }
            }
        } finally {
            // Creating forms installs a WinForms context, but the process self-test has no message loop.
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
            Directory.Delete(fixture, true);
        }
    }
}
