namespace GitWizardUI.ViewModels.Services;

internal sealed record UiEnvironment(IUiDispatcher Dispatcher, TimeProvider TimeProvider);
