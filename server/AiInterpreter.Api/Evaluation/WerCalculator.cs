using System.Text.RegularExpressions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Evaluation;

/// <summary>
/// Word Error Rate over scripted phrases (ARCH-015) — an objective <b>STT transcript</b> quality
/// signal, NOT a measure of semantic translation quality.
///
/// <para>Normalization: invariant-lowercase → strip Unicode punctuation (<c>\p{P}</c>, incl. ¿¡) →
/// collapse whitespace. Accents are <b>preserved</b> (accent-stripping is a documented opt-in we do
/// not take). Punctuation is removed (not replaced by a space), so apostrophes collapse contractions
/// the way STT output tends to ("don't"/"dont" both → "dont").</para>
///
/// <para><c>WER = (S + I + D) / N</c> over normalized word arrays, where S/I/D are attributed
/// individually via a DP edit-distance backtrace (precedence: match &gt; substitution &gt; deletion
/// &gt; insertion) and N is the reference word count. An empty normalized reference (N=0) is a
/// precondition violation (the reference is always a validated scripted phrase) → <c>ArgumentException</c>,
/// never a divide-by-zero.</para>
/// </summary>
public sealed partial class WerCalculator
{
    public WerResult Compute(string phraseId, string reference, string hypothesis)
    {
        var referenceWords = Normalize(reference);
        var hypothesisWords = Normalize(hypothesis);

        if (referenceWords.Length == 0)
        {
            throw new ArgumentException(
                "Reference must contain at least one word after normalization.", nameof(reference));
        }

        var (substitutions, insertions, deletions) = EditOps(referenceWords, hypothesisWords);
        var referenceWordCount = referenceWords.Length;
        var wer = (double)(substitutions + insertions + deletions) / referenceWordCount;

        return new WerResult(
            phraseId,
            reference,
            hypothesis,
            string.Join(' ', referenceWords),
            string.Join(' ', hypothesisWords),
            substitutions,
            insertions,
            deletions,
            referenceWordCount,
            wer);
    }

    private static string[] Normalize(string text)
    {
        var lowered = text.ToLowerInvariant();
        var depunctuated = PunctuationRegex().Replace(lowered, string.Empty);
        var collapsed = WhitespaceRegex().Replace(depunctuated, " ").Trim();
        return collapsed.Length == 0 ? [] : collapsed.Split(' ');
    }

    // DP edit distance + backtrace attributing each operation. Tie-break precedence on equal-cost
    // cells: match > substitution (diagonal) > deletion > insertion (documented for reproducibility).
    private static (int Substitutions, int Insertions, int Deletions) EditOps(string[] reference, string[] hypothesis)
    {
        var n = reference.Length;
        var m = hypothesis.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++)
        {
            d[i, 0] = i; // i deletions
        }

        for (var j = 0; j <= m; j++)
        {
            d[0, j] = j; // j insertions
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                d[i, j] = reference[i - 1] == hypothesis[j - 1]
                    ? d[i - 1, j - 1]
                    : 1 + Math.Min(d[i - 1, j - 1], Math.Min(d[i - 1, j], d[i, j - 1]));
            }
        }

        int substitutions = 0, insertions = 0, deletions = 0;
        var x = n;
        var y = m;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && reference[x - 1] == hypothesis[y - 1] && d[x, y] == d[x - 1, y - 1])
            {
                x--;
                y--; // match
            }
            else if (x > 0 && y > 0 && d[x, y] == d[x - 1, y - 1] + 1)
            {
                substitutions++;
                x--;
                y--;
            }
            else if (x > 0 && d[x, y] == d[x - 1, y] + 1)
            {
                deletions++;
                x--;
            }
            else
            {
                insertions++;
                y--;
            }
        }

        return (substitutions, insertions, deletions);
    }

    [GeneratedRegex(@"\p{P}")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
