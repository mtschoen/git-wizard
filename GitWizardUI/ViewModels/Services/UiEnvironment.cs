namespace GitWizardUI.ViewModels.Services;

sealed record UiEnvironment(IUiDispatcher Dispatcher, TimeProvider TimeProvider);
