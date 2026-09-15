using System.Globalization;
using Ripcord.Domain.Configuration;

namespace Ripcord.Cli.Rendering;

/// The one interactive screen in the binary, laid out for a 1024×768 KVM.
///
/// Three rules it is built to:
///
/// - **A run of questions is headed once.** Twelve VMs used to mean twelve copies of the
///   sentence explaining what a priority is, which reads the same as no explanation at all.
/// - **A small fixed set of answers belongs beside the prompt**, not in a numbered list. `1 y
///   / 2 n` above a yes-or-no question is ceremony, and ceremony is what gets skipped.
/// - **A list read off this host is numbered**, because there the number *is* the answer.
///
/// What Enter would accept is always the last thing before the cursor, in its own brackets: on
/// a re-run most of this is pressing Enter, and the value being accepted has to be visible at
/// the moment of pressing it.
public static class InitRenderer
{
    /// The head of the screen, written once.
    public static IReadOnlyList<string> Banner(
        string path, bool rewriting, Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        List<string> lines =
        [
            Ink.Bold(Layout.Pad("RIPCORD INIT", Layout.Width - path.Length)) + Ink.Faint(path),
            Ink.Faint(Layout.Line(Layout.Width)),
            "",
            Ink.Faint(rewriting
                ? "  Every question is answered with what the file says. Enter keeps it."
                : "  Nothing here yet. Enter takes the value in brackets."),
        ];

        return [.. lines.Select(line => (palette ?? Palette.None).Apply(line))];
    }

    /// Everything above the prompt: the section heading when it changes, the explanation, and
    /// the numbered list when there is one.
    public static IReadOnlyList<string> Lines(
        InterviewQuestion question, string? previousGroup = null, Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(question);

        List<string> lines = [""];

        if (question.Group.Length > 0 && question.Group != previousGroup)
        {
            lines.Add(Ink.Bold(question.Group));
            lines.Add(Ink.Faint(Layout.Line(Layout.Width)));
        }

        lines.AddRange(question.Explanation.Select(line => Ink.Faint($"  {line}")));

        if (question.Explanation.Count > 0)
        {
            lines.Add("");
        }

        if (question.Listed)
        {
            // Numbered from one, because the answer is typed by a person rather than indexed
            // by a program — and the number is what gets typed, so it is what stands out.
            lines.AddRange(question.Choices.Select((choice, index) =>
                "    "
                + Ink.Cyan($"{(index + 1).ToString(CultureInfo.InvariantCulture),2}")
                + $"  {choice}"));

            lines.Add("");
        }

        return [.. lines.Select(line => (palette ?? Palette.None).Apply(line))];
    }

    public static string Prompt(InterviewQuestion question, Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(question);

        string prompt = "  " + Ink.Bold(question.Prompt) + Options(question) + Default(question)
            + Ink.Cyan(" > ");

        return (palette ?? Palette.None).Apply(prompt);
    }

    /// The fixed words, beside the prompt. Never for a list read off the host: those are
    /// numbered above, and repeating twelve VM names here would fill the line.
    private static string Options(InterviewQuestion question) =>
        question is { Listed: false, Choices.Count: > 0 }
            ? Ink.Faint("  " + string.Join("/", question.Choices))
            : "";

    /// What Enter accepts, told apart from the options it is one of.
    private static string Default(InterviewQuestion question) =>
        question.Default is { Length: > 0 } answer ? Ink.Faint($" [{answer}]") : "";
}
