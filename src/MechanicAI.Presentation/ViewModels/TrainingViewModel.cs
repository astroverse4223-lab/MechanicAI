using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Training;
using MechanicAI.Domain.Entities;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Courses and lessons, quizzes, spaced-repetition flashcards, and computed practice exercises.</summary>
public sealed partial class TrainingViewModel(ITrainingGateway training, ISettingsStore settings) : ViewModelBase
{
    private TrainingCourse? _course;
    private readonly Queue<Flashcard> _cardQueue = new();
    private Flashcard? _currentCard;
    private int _cardsCorrect;
    private int _cardsReviewed;

    public ObservableCollection<CourseItem> Courses { get; } = [];

    public ObservableCollection<LessonItem> Lessons { get; } = [];

    public ObservableCollection<QuizItem> Quizzes { get; } = [];

    public ObservableCollection<WarningItem> LessonSafety { get; } = [];

    public ObservableCollection<QuizQuestionItem> QuizQuestions { get; } = [];

    public ObservableCollection<ExerciseItem> Exercises { get; } = [];

    public IReadOnlyList<string> ExerciseTypes { get; } = ExerciseGenerator.Types.Select(ExerciseGenerator.Describe).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCourse))]
    public partial CourseItem? SelectedCourse { get; set; }

    public bool HasCourse => SelectedCourse is not null;

    [ObservableProperty]
    public partial LessonItem? SelectedLesson { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLesson))]
    [NotifyCanExecuteChangedFor(nameof(MarkLessonCompleteCommand), nameof(GenerateAiQuizCommand))]
    public partial Guid? CurrentLessonId { get; set; }

    public bool HasLesson => CurrentLessonId is not null;

    [ObservableProperty]
    public partial string LessonTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LessonBody { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LessonMeta { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LessonCompleted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousLesson))]
    [NotifyCanExecuteChangedFor(nameof(PreviousLessonCommand))]
    public partial Guid? PreviousLessonId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextLesson))]
    [NotifyCanExecuteChangedFor(nameof(NextLessonCommand))]
    public partial Guid? NextLessonId { get; set; }

    public bool HasPreviousLesson => PreviousLessonId is not null;

    public bool HasNextLesson => NextLessonId is not null;

    // quiz
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuizActive))]
    [NotifyCanExecuteChangedFor(nameof(SubmitQuizCommand))]
    public partial QuizItem? ActiveQuiz { get; set; }

    public bool IsQuizActive => ActiveQuiz is not null;

    [ObservableProperty]
    public partial string? QuizResultText { get; set; }

    // flashcards
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCard))]
    public partial string? CardFront { get; set; }

    public bool HasCard => CardFront is not null;

    [ObservableProperty]
    public partial string? CardBack { get; set; }

    [ObservableProperty]
    public partial bool IsCardFlipped { get; set; }

    [ObservableProperty]
    public partial string FlashcardStatus { get; set; } = string.Empty;

    // exercises
    [ObservableProperty]
    public partial int ExerciseTypeIndex { get; set; }

    [ObservableProperty]
    public partial string? ExerciseScore { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RunAsync(LoadCoursesAsync);
        if (parameter is Guid lessonId) await OpenLessonByIdAsync(lessonId);
    }

    private async Task LoadCoursesAsync(CancellationToken ct)
    {
        var selected = SelectedCourse?.Id;
        var courses = await training.ListCoursesAsync(ct);
        Courses.Clear();
        foreach (var c in courses) Courses.Add(CourseItem.From(c));
        _ignoreCourseSelection = true;
        SelectedCourse = Courses.FirstOrDefault(c => c.Id == selected);
        _ignoreCourseSelection = false;
    }

    private bool _ignoreCourseSelection;

    partial void OnSelectedCourseChanged(CourseItem? value)
    {
        if (_ignoreCourseSelection || value is null) return;
        _ = RunAsync(ct => LoadCourseAsync(value.Id, ct));
    }

    private async Task LoadCourseAsync(Guid courseId, CancellationToken ct)
    {
        _course = await training.GetCourseAsync(courseId, ct);
        Lessons.Clear();
        Quizzes.Clear();
        if (_course is null) return;
        foreach (var l in _course.Lessons.OrderBy(l => l.SortOrder))
        {
            Lessons.Add(new LessonItem(l.Id, l.Title, $"{l.EstimatedMinutes} min", l.CompletedUtc is not null));
        }

        foreach (var q in _course.Quizzes) Quizzes.Add(new QuizItem(q.Id, q.Title, q.Questions.Count, q.IsAiGenerated));
        FlashcardStatus = $"{_course.Flashcards.Count(f => f.DueUtc <= DateTime.UtcNow)} card(s) due in this course";
    }

    partial void OnSelectedLessonChanged(LessonItem? value)
    {
        if (value is not null && value.Id != CurrentLessonId) _ = OpenLessonByIdAsync(value.Id);
    }

    [RelayCommand]
    private Task OpenLessonByIdAsync(Guid? lessonId) => lessonId is not { } id
        ? Task.CompletedTask
        : RunAsync(async ct =>
        {
            var view = await training.GetLessonAsync(id, ct);
            if (view is null)
            {
                ShowError(Error.NotFound("Lesson"));
                return;
            }

            if (_course?.Id != view.Course.Id)
            {
                _ignoreCourseSelection = true;
                SelectedCourse = Courses.FirstOrDefault(c => c.Id == view.Course.Id);
                _ignoreCourseSelection = false;
                await LoadCourseAsync(view.Course.Id, ct);
            }

            CurrentLessonId = view.Lesson.Id;
            LessonTitle = view.Lesson.Title;
            LessonBody = view.Lesson.BodyMarkdown;
            LessonMeta = $"{view.Course.Title} · {view.Lesson.EstimatedMinutes} min";
            LessonCompleted = view.Lesson.CompletedUtc is not null;
            PreviousLessonId = view.PreviousLessonId;
            NextLessonId = view.NextLessonId;
            LessonSafety.Clear();
            foreach (var w in view.Safety) LessonSafety.Add(WarningItem.From(w));
            SelectedLesson = Lessons.FirstOrDefault(l => l.Id == id);
        });

    [RelayCommand(CanExecute = nameof(HasPreviousLesson))]
    private Task PreviousLessonAsync() => OpenLessonByIdAsync(PreviousLessonId);

    [RelayCommand(CanExecute = nameof(HasNextLesson))]
    private Task NextLessonAsync() => OpenLessonByIdAsync(NextLessonId);

    [RelayCommand(CanExecute = nameof(HasLesson))]
    private Task MarkLessonCompleteAsync() => RunAsync(async ct =>
    {
        if (CurrentLessonId is not { } id) return;
        await training.MarkLessonCompleteAsync(id, !LessonCompleted, ct);
        LessonCompleted = !LessonCompleted;
        if (_course is not null) await LoadCourseAsync(_course.Id, ct);
        await LoadCoursesAsync(ct);
    });

    // ---------------------------------------------------------------- quizzes

    [RelayCommand]
    private void StartQuiz(QuizItem? quiz)
    {
        if (quiz is null || _course is null) return;
        var entity = _course.Quizzes.FirstOrDefault(q => q.Id == quiz.Id);
        if (entity is null) return;
        QuizQuestions.Clear();
        for (var i = 0; i < entity.Questions.Count; i++) QuizQuestions.Add(new QuizQuestionItem(i, entity.Questions[i].Question, entity.Questions[i].Choices));
        QuizResultText = null;
        ActiveQuiz = quiz;
    }

    [RelayCommand(CanExecute = nameof(IsQuizActive))]
    private Task SubmitQuizAsync() => RunAsync(async ct =>
    {
        if (ActiveQuiz is not { } quiz) return;
        var unanswered = QuizQuestions.Count(q => q.SelectedIndex < 0);
        if (unanswered > 0)
        {
            ShowError(Error.Validation($"Answer every question first ({unanswered} left)."));
            return;
        }

        var result = await training.SubmitQuizAsync(quiz.Id, QuizQuestions.Select(q => q.SelectedIndex).ToList(), Clean(settings.Current.Diagnostics.TechnicianName), ct);
        if (!Check(result)) return;
        var value = result.Value!;
        foreach (var q in value.Questions)
        {
            if (q.Index < 0 || q.Index >= QuizQuestions.Count) continue;
            var item = QuizQuestions[q.Index];
            var correctText = q.CorrectIndex >= 0 && q.CorrectIndex < item.Choices.Count ? item.Choices[q.CorrectIndex] : "?";
            item.Feedback = (q.Correct ? "Correct. " : $"Incorrect — answer: {correctText}. ") + (q.Explanation ?? string.Empty);
        }

        QuizResultText = string.Create(CultureInfo.CurrentCulture, $"{value.Correct}/{value.Total} ({value.Percent:0}%) — {(value.Passed ? "passed" : "not passed yet")}");
        await LoadCoursesAsync(ct);
    });

    [RelayCommand]
    private void CloseQuiz()
    {
        ActiveQuiz = null;
        QuizQuestions.Clear();
        QuizResultText = null;
    }

    [RelayCommand(CanExecute = nameof(HasLesson))]
    private Task GenerateAiQuizAsync() => RunAsync(async ct =>
    {
        if (CurrentLessonId is not { } id) return;
        var result = await training.GenerateAiQuizAsync(id, 5, ct);
        if (!Check(result)) return;
        if (_course is not null) await LoadCourseAsync(_course.Id, ct);
        var quiz = Quizzes.FirstOrDefault(q => q.Id == result.Value);
        ShowNotice("AI quiz created", "The quiz is labeled AI-generated. Check answers against the lesson.", NoticeSeverity.Informational);
        if (quiz is not null) StartQuiz(quiz);
    }, "Generating quiz…");

    // ---------------------------------------------------------------- flashcards

    [RelayCommand]
    private Task StartFlashcardsAsync() => RunAsync(async ct =>
    {
        var cards = await training.GetDueFlashcardsAsync(_course?.Id, ct);
        _cardQueue.Clear();
        foreach (var c in cards) _cardQueue.Enqueue(c);
        _cardsCorrect = _cardsReviewed = 0;
        NextCard();
    });

    private void NextCard()
    {
        IsCardFlipped = false;
        if (_cardQueue.Count == 0)
        {
            _currentCard = null;
            CardFront = null;
            CardBack = null;
            FlashcardStatus = _cardsReviewed == 0 ? "No cards are due. Come back later." : $"Session done: {_cardsCorrect}/{_cardsReviewed} correct.";
            return;
        }

        _currentCard = _cardQueue.Dequeue();
        CardFront = _currentCard.Front;
        CardBack = _currentCard.Back;
        FlashcardStatus = $"{_cardQueue.Count + 1} card(s) left";
    }

    [RelayCommand]
    private void FlipCard() => IsCardFlipped = !IsCardFlipped;

    [RelayCommand]
    private Task CardCorrectAsync() => ReviewCardAsync(true);

    [RelayCommand]
    private Task CardIncorrectAsync() => ReviewCardAsync(false);

    private Task ReviewCardAsync(bool correct) => RunAsync(async ct =>
    {
        if (_currentCard is null) return;
        await training.ReviewFlashcardAsync(_currentCard.Id, correct, ct);
        _cardsReviewed++;
        if (correct) _cardsCorrect++;
        NextCard();
    });

    // ---------------------------------------------------------------- exercises

    [RelayCommand]
    private void GenerateExercises()
    {
        var index = Math.Clamp(ExerciseTypeIndex, 0, ExerciseGenerator.Types.Count - 1);
        Exercises.Clear();
        foreach (var e in training.GenerateExercises([ExerciseGenerator.Types[index]], 5)) Exercises.Add(new ExerciseItem(e));
        ExerciseScore = null;
    }

    [RelayCommand]
    private Task CheckExercisesAsync() => RunAsync(async ct =>
    {
        if (Exercises.Count == 0) return;
        var correct = 0;
        foreach (var item in Exercises)
        {
            var response = item.IsMultipleChoice ? item.SelectedChoice.ToString(CultureInfo.InvariantCulture) : item.Response ?? string.Empty;
            var ok = item.Exercise.Check(response);
            item.IsCorrect = ok;
            item.Feedback = (ok ? "Correct. " : "Not quite. ") + item.Exercise.Explanation;
            if (ok) correct++;
        }

        ExerciseScore = $"{correct}/{Exercises.Count} correct";
        var courseKey = _course?.Key ?? "practice";
        await training.RecordExerciseSetAsync(courseKey, correct, Exercises.Count, Clean(settings.Current.Diagnostics.TechnicianName), ct);
    });
}
