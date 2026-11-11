using SecurityToolkit.Cli;
using System.Text;

// Ensure non-ASCII characters (paths, scanner output) render/parse correctly regardless of the host
// console's default code page. Setting OutputEncoding is safe in practice; InputEncoding
// can throw when no real console is attached (e.g. output redirected to a file/pipe in CI),
// which is a known Windows console limitation, not an error worth failing the process for.
try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached (redirected/piped stdin or stdout) — nothing to do.
}

// The external pipelines rely on the process exit code, not stdout content: a command action's
// returned int (0/1/2, per each command's own ScanOutcome/InstallOutcome/RestoreOutcome contract),
// System.CommandLine's non-zero code on a parse error, and 2 for an exception that escapes a command.
return await CliApp.InvokeAsync(args);
