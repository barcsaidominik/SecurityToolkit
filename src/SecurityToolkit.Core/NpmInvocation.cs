namespace SecurityToolkit.Core;

/// <summary>
/// How to invoke npm without a shell. On Windows, npm.cmd is a shell wrapper that cannot be
/// launched with UseShellExecute=false, so npm is dispatched by running node directly against
/// npm's own npm-cli.js script; on other platforms npm's real executable can be invoked directly.
/// </summary>
/// <param name="FileName">The executable to start.</param>
/// <param name="PrefixArguments">
/// Arguments that must precede the caller's own arguments (e.g. the path to npm-cli.js on Windows;
/// empty elsewhere).
/// </param>
public sealed record NpmInvocation(string FileName, IReadOnlyList<string> PrefixArguments);
