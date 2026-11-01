namespace Vedar.Core.Abstractions;

/// <summary>
/// Initial prompts for Whisper. A prompt with the command vocabulary biases recognition toward the words
/// VEDAR understands, which markedly improves short commands (see docs/SPEECH_BENCHMARKS.md).
/// It is not used inside an AI conversation, where the user may say anything.
/// </summary>
public static class RecognitionPrompts
{
    public const string WakeWord = "Ведар";

    public const string Commands =
        "Ведар, колко е часът? Коя дата сме? Отвори браузъра. Отвори бележника. Отвори настройките. " +
        "Прочети файл. Повтори. Спри. Усили звука. Намали звука. Искам да поговорим. Затвори разговора. Помощ.";
}
