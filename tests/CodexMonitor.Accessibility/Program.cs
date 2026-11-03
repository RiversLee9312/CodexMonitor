using System.Text.Json;
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;

if (args.Length != 4) throw new ArgumentException("Expected process ID, window handle, output directory and scan ID.");
var processId = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
var handle = new IntPtr(long.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
var config = Config.Builder.ForProcessId(processId).WithOutputFileFormat(OutputFileFormat.A11yTest).WithAlwaysSaveTestFile().WithOutputDirectory(output).Build();
var scanner = ScannerFactory.CreateScanner(config);
var result = scanner.Scan(new ScanOptions(scanId: args[3], scanRootWindowHandle: handle));
var report = result.WindowScanOutputs.Select(window => new
{
    window.ErrorCount, File = window.OutputFile.A11yTest,
    Errors = window.Errors.Select(error => new { Rule = error.Rule.ID.ToString(), error.Rule.Description, error.Rule.HowToFix, error.Element.Properties }).ToArray()
}).ToArray();
await File.WriteAllTextAsync(Path.Combine(output, args[3] + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Scanned {report.Length} windows; {report.Sum(w => w.ErrorCount)} errors.");
return report.Length > 0 && report.Sum(w => w.ErrorCount) == 0 ? 0 : 1;
