using AiInterpreter.Api.Evaluation;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// B.6 — WerCalculator + EvaluationPhraseStore (ARCH-015). CRITICAL tier (ARCH-020): the ARCH-015
// case list IS the spec — perfect / deletion / insertion / substitution / empty-hypothesis /
// empty-reference-rejected / punctuation+casing normalization, plus accent-preserve and combined
// S/I/D attribution. WER is an STT-transcript quality signal only — not semantic translation quality.
public class WerCalculatorTests
{
    private static WerResult Compute(string reference, string hypothesis)
        => new WerCalculator().Compute("p1", reference, hypothesis);

    [Fact]
    public void perfect_match_is_zero()
    {
        var r = Compute("the quick brown fox", "the quick brown fox");

        Assert.Equal(0, r.Substitutions);
        Assert.Equal(0, r.Insertions);
        Assert.Equal(0, r.Deletions);
        Assert.Equal(4, r.ReferenceWordCount);
        Assert.Equal(0.0, r.Wer);
        Assert.Equal("p1", r.PhraseId);
    }

    [Fact]
    public void one_deletion()
    {
        // hyp drops "brown".
        var r = Compute("the quick brown fox", "the quick fox");

        Assert.Equal(1, r.Deletions);
        Assert.Equal(0, r.Substitutions);
        Assert.Equal(0, r.Insertions);
        Assert.Equal(0.25, r.Wer); // 1/4
    }

    [Fact]
    public void one_insertion()
    {
        // hyp adds "brown".
        var r = Compute("the quick fox", "the quick brown fox");

        Assert.Equal(1, r.Insertions);
        Assert.Equal(0, r.Substitutions);
        Assert.Equal(0, r.Deletions);
        Assert.Equal(1.0 / 3, r.Wer, 10);
    }

    [Fact]
    public void one_substitution()
    {
        // "brown" -> "red".
        var r = Compute("the quick brown fox", "the quick red fox");

        Assert.Equal(1, r.Substitutions);
        Assert.Equal(0, r.Insertions);
        Assert.Equal(0, r.Deletions);
        Assert.Equal(0.25, r.Wer); // 1/4
    }

    [Fact]
    public void empty_hypothesis_all_deleted()
    {
        var r = Compute("the quick brown fox", "");

        Assert.Equal(4, r.Deletions);
        Assert.Equal(0, r.Substitutions);
        Assert.Equal(0, r.Insertions);
        Assert.Equal(1.0, r.Wer);
    }

    [Fact]
    public void empty_reference_rejected_no_divide_by_zero()
    {
        var calc = new WerCalculator();

        // Empty + punctuation-only (0 normalized words) are precondition violations → ArgumentException,
        // never NaN/Infinity/DivideByZero (Q1: the reference is always a validated scripted phrase).
        Assert.Throws<ArgumentException>(() => calc.Compute("p1", "", "hello"));
        Assert.Throws<ArgumentException>(() => calc.Compute("p1", "  ¿?  ", "hello"));
    }

    [Fact]
    public void punctuation_and_casing_normalized()
    {
        var r = Compute("Hello, World!", "hello world");

        Assert.Equal(0.0, r.Wer);
        Assert.Equal("hello world", r.NormalizedReference);
        Assert.Equal("hello world", r.NormalizedHypothesis);
    }

    [Fact]
    public void accents_preserved_in_normalization()
    {
        // Spanish punctuation (¿¡.) stripped + lowercased, but accented letters / ñ kept intact.
        var r = Compute("Necesito Ayuda.", "necesito ayuda");
        Assert.Equal(0.0, r.Wer);

        var accented = Compute("El Niño Pequeño", "el niño pequeño");
        Assert.Equal(0.0, accented.Wer);
        Assert.Equal("el niño pequeño", accented.NormalizedReference); // ñ NOT stripped to n
    }

    [Fact]
    public void sid_attribution_combined()
    {
        // ref=[alpha,beta,gamma,delta,epsilon] hyp=[alpha,zeta,delta,epsilon,omega]. The minimal
        // edit (dist 3) is forced to exactly 1 sub + 1 del + 1 ins (equal ref/hyp length ⇒ I==D;
        // LCS=3 ⇒ the only dist-3 composition is 1/1/1), so the counts hold under any tie-break.
        var r = Compute("alpha beta gamma delta epsilon", "alpha zeta delta epsilon omega");

        Assert.Equal(1, r.Substitutions);
        Assert.Equal(1, r.Insertions);
        Assert.Equal(1, r.Deletions);
        Assert.Equal(5, r.ReferenceWordCount);
        Assert.Equal(0.6, r.Wer); // 3/5
    }

    [Fact]
    public void phrase_store_loads_phrases_en_and_es_present()
    {
        var store = new EvaluationPhraseStore(Path.Combine(AppContext.BaseDirectory, "Evaluation", "evaluation-phrases.json"));

        Assert.True(store.IsLoaded, store.LoadError);
        Assert.InRange(store.Phrases.Count, 8, 12);
        Assert.Contains(store.Phrases, p => p.Language == LanguageCode.En);
        Assert.Contains(store.Phrases, p => p.Language == LanguageCode.Es);

        var first = store.Phrases[0];
        Assert.Equal(first, store.GetById(first.PhraseId));
        Assert.Null(store.GetById("does-not-exist"));

        var spanish = store.GetByLanguage(LanguageCode.Es);
        Assert.NotEmpty(spanish);
        Assert.All(spanish, p => Assert.Equal(LanguageCode.Es, p.Language));
    }

    [Fact]
    public void wer_exceeds_one_when_hypothesis_longer_than_reference()
    {
        // No clamping: a hypothesis with no shared words and extra tokens yields WER > 1.0.
        // ref=[one,two] hyp=[three,four,five,six] → 2 subs + 2 ins (0 matches ⇒ unique minimum).
        var r = Compute("one two", "three four five six");

        Assert.Equal(2, r.Substitutions);
        Assert.Equal(2, r.Insertions);
        Assert.Equal(0, r.Deletions);
        Assert.Equal(2, r.ReferenceWordCount);
        Assert.Equal(2.0, r.Wer); // 4/2 — must NOT be clamped to 1.0
    }

    [Fact]
    public void phrase_store_missing_or_malformed_file_degrades()
    {
        // Missing file → empty store, no crash; LoadError carries the reason (lesson §3 contract).
        var missing = new EvaluationPhraseStore(Path.Combine(AppContext.BaseDirectory, "no-such-phrases.json"));
        Assert.False(missing.IsLoaded);
        Assert.Empty(missing.Phrases);
        Assert.Null(missing.GetById("anything"));
        Assert.False(string.IsNullOrWhiteSpace(missing.LoadError));

        // Malformed JSON → empty store, no crash, LoadError set.
        var temp = Path.Combine(Path.GetTempPath(), $"phrases-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(temp, "{ this is not valid json ");
        try
        {
            var bad = new EvaluationPhraseStore(temp);
            Assert.False(bad.IsLoaded);
            Assert.Empty(bad.Phrases);
            Assert.False(string.IsNullOrWhiteSpace(bad.LoadError));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void phrase_store_oversized_file_degrades()
    {
        // The 1 MB size guard fires before the read (no OOM risk on a misconfigured huge path).
        var temp = Path.Combine(Path.GetTempPath(), $"phrases-big-{Guid.NewGuid():N}.json");
        File.WriteAllText(temp, new string(' ', (1024 * 1024) + 16));
        try
        {
            var result = EvaluationPhraseStore.Load(temp);
            Assert.False(result.IsSuccess);
            Assert.Contains("too large", result.Error);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
