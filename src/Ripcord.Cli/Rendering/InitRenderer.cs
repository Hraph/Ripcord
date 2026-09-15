using System.Globalization;
using Ripcord.Domain.Configuration;

namespace Ripcord.Cli.Rendering;

/// One interview question, laid out for a 1024×768 KVM.
///
/// The explanation goes above the list and the list above the prompt, so the thing being
/// answered is the last line on the screen when the cursor stops. The default sits in the
/// prompt itself: somebody re-running this a year later presses Enter through most of it, and
/// what Enter is about to accept has to be visible at the moment of pressing it.
public static class InitRenderer
{
    public static IReadOnlyList<string> Lines(InterviewQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        List<string> lines = [""];

        lines.AddRange(question.Explanation.Select(line => $"  {line}"));

        if (question.Choices.Count > 0)
        {
            lines.Add("");

            // Numbered from one, because the answer is typed by a person rather than indexed
            // by a program.
            lines.AddRange(question.Choices.Select((choice, index) =>
                $"    {(index + 1).ToString(CultureInfo.InvariantCulture),2}  {choice}"));

            lines.Add("");
        }

        return lines;
    }

    public static string Prompt(InterviewQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        return question.Default is { Length: > 0 } answer
            ? $"  {question.Prompt} [{answer}] "
            : $"  {question.Prompt} ";
    }
}
