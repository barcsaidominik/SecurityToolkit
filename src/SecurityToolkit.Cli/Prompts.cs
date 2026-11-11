namespace SecurityToolkit.Cli;

/// <summary>
/// Interactive console prompts (yes/no, value, target selection). Kept entirely in the CLI
/// layer (never in Core) — Core's
/// <c>SecurityToolkitInstaller</c> always takes fully-resolved input, exactly as documented on
/// that class; this layer is what resolves interactive answers into that input when
/// <c>--non-interactive</c> wasn't passed.
/// </summary>
public interface IConsolePrompts
{
    bool ReadYesNo(string prompt, bool defaultValue);

    string ReadValue(string prompt, string defaultValue);

    /// <summary>Reads an integer value, reprompting on a non-integer answer.</summary>
    int ReadInt(string prompt, int defaultValue);

    /// <summary>
    /// Prints <paramref name="paths"/> as a numbered list and reads a
    /// comma-separated selection (Enter = all, "0" = none), retrying on invalid input.
    /// </summary>
    IReadOnlyList<string> SelectTargets(string name, IReadOnlyList<string> paths, IReadOnlyList<string> existing);
}

/// <summary>Real <see cref="Console"/>-backed implementation.</summary>
public sealed class ConsolePrompts : IConsolePrompts
{
    public bool ReadYesNo(string prompt, bool defaultValue)
    {
        var label = defaultValue ? "Y/n" : "y/N";
        while (true)
        {
            Console.Write($"{prompt} [{label}]: ");
            var answer = Console.ReadLine();
            if (string.IsNullOrEmpty(answer))
            {
                return defaultValue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(answer, "^(y|yes)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(answer, "^(n|no)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return false;
            }
        }
    }

    public string ReadValue(string prompt, string defaultValue)
    {
        Console.Write($"{prompt} [{defaultValue}]: ");
        var answer = Console.ReadLine();
        return string.IsNullOrEmpty(answer) ? defaultValue : answer;
    }

    public int ReadInt(string prompt, int defaultValue)
    {
        while (true)
        {
            var answer = ReadValue(prompt, defaultValue.ToString());
            if (int.TryParse(answer, out var value))
            {
                return value;
            }
        }
    }

    public IReadOnlyList<string> SelectTargets(string name, IReadOnlyList<string> paths, IReadOnlyList<string> existing)
    {
        if (paths.Count == 0)
        {
            return existing;
        }

        Console.WriteLine();
        Console.WriteLine(name);
        for (var i = 0; i < paths.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {paths[i]}");
        }

        while (true)
        {
            Console.Write("Numbers separated by commas, Enter: all, 0: none: ");
            var answer = Console.ReadLine();
            if (string.IsNullOrEmpty(answer))
            {
                return paths;
            }

            if (answer.Trim() == "0")
            {
                return Array.Empty<string>();
            }

            var parts = answer.Split(',', StringSplitOptions.TrimEntries);
            var indexes = new List<int>();
            var valid = true;

            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var n) || n < 1 || n > paths.Count)
                {
                    valid = false;
                    break;
                }

                indexes.Add(n - 1);
            }

            if (valid)
            {
                return indexes.Distinct().Select(i => paths[i]).ToList();
            }
        }
    }
}

/// <summary>
/// Non-interactive answers: always the given default, and for target selection the existing
/// selection if any, otherwise every discovered path.
/// </summary>
public sealed class NonInteractivePrompts : IConsolePrompts
{
    public bool ReadYesNo(string prompt, bool defaultValue) => defaultValue;

    public string ReadValue(string prompt, string defaultValue) => defaultValue;

    public int ReadInt(string prompt, int defaultValue) => defaultValue;

    public IReadOnlyList<string> SelectTargets(string name, IReadOnlyList<string> paths, IReadOnlyList<string> existing)
        => existing.Count > 0 ? existing : paths;
}
