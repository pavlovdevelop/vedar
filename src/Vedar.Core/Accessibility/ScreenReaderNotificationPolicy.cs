using Vedar.Core.Abstractions;
using Vedar.Core.Settings;

namespace Vedar.Core.Accessibility;

/// <summary>
/// Decides whether a message should also be sent to the screen reader (NVDA, Narrator) as a UI Automation
/// notification. Goal: the user never hears the same sentence twice — once from VEDAR's voice and once from
/// the screen reader — unless they ask for it.
/// </summary>
public static class ScreenReaderNotificationPolicy
{
    public static bool ShouldNotify(ScreenReaderNotificationMode mode, UiNotificationKind kind, bool spokenByVedar, bool screenReaderActive)
    {
        if (!screenReaderActive || mode == ScreenReaderNotificationMode.Off)
        {
            return false;
        }

        return mode switch
        {
            ScreenReaderNotificationMode.All => true,
            ScreenReaderNotificationMode.Important => kind is UiNotificationKind.Error or UiNotificationKind.Important,
            // Auto: only what VEDAR could not say itself (e.g. the voice failed or the text is shown only).
            _ => !spokenByVedar && kind != UiNotificationKind.Status,
        };
    }
}
