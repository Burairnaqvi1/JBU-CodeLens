namespace JBU.CodeLens.Shared.Models;

/// <summary>
/// A coarse classification of what role a class plays in the codebase, used to group and
/// visualize classes by responsibility.
/// </summary>
public enum CodeCategory
{
    /// <summary>
    /// Domain/business logic: the default category for classes that model the application's
    /// core behavior and data, rather than UI presentation or generic helpers.
    /// </summary>
    BusinessLogic,

    /// <summary>
    /// Presentation/UI logic: classes tied to the graphical user interface, such as windows,
    /// views, controls, and dialogs.
    /// </summary>
    GuiLogic,

    /// <summary>
    /// Utility code: stateless helpers, extension method bags, and similar support classes that
    /// provide reusable functionality without holding domain state.
    /// </summary>
    Utility,
}
