using System.Globalization;
using System.Text.RegularExpressions;
using MechanicAI.Application.Training;

namespace MechanicAI.Application.Tests.Training;

public class ExerciseGeneratorTests
{
    public static TheoryData<string> AllTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in ExerciseGenerator.Types) data.Add(type);
        return data;
    }

    [Fact]
    public void Generate_FiltersUnknownTypesAndIsDeterministic()
    {
        var first = ExerciseGenerator.Generate(["ohms-law", "bogus"], 5, seed: 7);
        var second = ExerciseGenerator.Generate(["ohms-law", "bogus"], 5, seed: 7);

        Assert.Equal(5, first.Count);
        Assert.All(first, e => Assert.Equal("ohms-law", e.Type));
        Assert.Equal(["ohms-law-7-0", "ohms-law-7-1", "ohms-law-7-2", "ohms-law-7-3", "ohms-law-7-4"], first.Select(e => e.Key));
        Assert.Equal(first.Select(e => e.Prompt), second.Select(e => e.Prompt));
    }

    [Fact]
    public void Generate_CyclesThroughAllTypesWhenNoneAreValid()
    {
        var exercises = ExerciseGenerator.Generate(["bogus"], ExerciseGenerator.Types.Count, seed: 1);

        Assert.Equal(ExerciseGenerator.Types, exercises.Select(e => e.Type));
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void EveryGeneratedExerciseAcceptsItsOwnAnswerAndRejectsWrongOnes(string type)
    {
        for (var seed = 0; seed < 60; seed++)
        {
            var exercise = ExerciseGenerator.Create(type, new Random(seed), $"k{seed}");

            Assert.Equal(type, exercise.Type);
            Assert.False(string.IsNullOrWhiteSpace(exercise.Prompt));
            Assert.False(string.IsNullOrWhiteSpace(exercise.Explanation));

            if (exercise.IsMultipleChoice)
            {
                Assert.NotNull(exercise.CorrectChoice);
                Assert.InRange(exercise.CorrectChoice.Value, 0, exercise.Choices.Count - 1);
                Assert.Null(exercise.NumericAnswer);
                Assert.True(exercise.Check(exercise.CorrectChoice.Value.ToString(CultureInfo.InvariantCulture)));
                Assert.False(exercise.Check(((exercise.CorrectChoice.Value + 1) % exercise.Choices.Count).ToString(CultureInfo.InvariantCulture)));
                Assert.False(exercise.Check("not a number"));
            }
            else
            {
                var answer = exercise.NumericAnswer!.Value;
                Assert.True(exercise.Check(answer.ToString(CultureInfo.InvariantCulture) + " " + exercise.Unit));
                Assert.False(exercise.Check((answer + Math.Max(1, Math.Abs(answer))).ToString(CultureInfo.InvariantCulture)));
                Assert.False(exercise.Check("no digits"));
            }
        }
    }

    [Fact]
    public void FuelTrimAnswerMatchesTheNumbersInThePrompt()
    {
        var regex = new Regex(@"STFT (?<s>[+-]\d+\.\d)%, LTFT (?<l>[+-]\d+\.\d)%");
        for (var seed = 0; seed < 200; seed++)
        {
            var exercise = ExerciseGenerator.Create("fuel-trim", new Random(seed), "k");
            var m = regex.Match(exercise.Prompt);
            Assert.True(m.Success, exercise.Prompt);

            var total = double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) + double.Parse(m.Groups["l"].Value, CultureInfo.InvariantCulture);
            var expected = total > 10 ? 0 : total < -10 ? 1 : 2;
            Assert.Equal(expected, exercise.CorrectChoice);
        }
    }

    [Fact]
    public void VoltageDropAnswerMatchesTheNumbersInThePrompt()
    {
        var regex = new Regex(@"reads (?<src>\d+\.\d\d) V and the load's power input reads (?<load>\d+\.\d\d) V");
        for (var seed = 0; seed < 200; seed++)
        {
            var exercise = ExerciseGenerator.Create("voltage-drop", new Random(seed), "k");
            var m = regex.Match(exercise.Prompt);
            Assert.True(m.Success, exercise.Prompt);

            var drop = double.Parse(m.Groups["src"].Value, CultureInfo.InvariantCulture) - double.Parse(m.Groups["load"].Value, CultureInfo.InvariantCulture);
            // Values exactly at the 0.5 V guideline are ambiguous after rounding; skip them.
            if (Math.Abs(drop - 0.5) < 0.011) continue;
            Assert.Equal(drop > 0.5 ? 1 : 0, exercise.CorrectChoice);
        }
    }

    [Fact]
    public void Check_UsesMinimumAbsoluteTolerance()
    {
        var exercise = new Exercise("k", "ohms-law", "p", 0.2, "A", 3, [], null, "e");

        Assert.True(exercise.Check("0.25"));
        Assert.False(exercise.Check("0.26"));
        Assert.False(exercise.Check("1,000"));
    }

    [Fact]
    public void Check_StripsThousandsSeparatorsAndUnits()
    {
        var exercise = new Exercise("k", "unit-pressure", "p", 1379, "kPa", 1, [], null, "e");

        Assert.True(exercise.Check("1,380 kPa"));
    }

    [Fact]
    public void Check_AcceptsUnitsContainingDashesAndNegativeAnswers()
    {
        Assert.True(new Exercise("k", "unit-torque", "p", 35, "lb-ft", 1, [], null, "e").Check("35 lb-ft"));
        Assert.True(new Exercise("k", "unit-temperature", "p", -12.5, "°C", 1, [], null, "e").Check("-12.5 °C"));
        Assert.False(new Exercise("k", "unit-temperature", "p", -12.5, "°C", 1, [], null, "e").Check("12.5 °C"));
    }

    [Fact]
    public void FuelTrimPromptNeverShowsDoubleSign()
    {
        for (var seed = 0; seed < 500; seed++)
        {
            var exercise = ExerciseGenerator.Create("fuel-trim", new Random(seed), "k");
            Assert.DoesNotContain("-+", exercise.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("-+", exercise.Explanation, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ohms-law", "Ohm's law")]
    [InlineData("unit-temperature", "Temperature conversion")]
    [InlineData("custom", "custom")]
    public void Describe_ReturnsFriendlyName(string type, string expected)
    {
        Assert.Equal(expected, ExerciseGenerator.Describe(type));
    }
}
