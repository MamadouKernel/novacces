using Microsoft.Extensions.DependencyInjection;
using NovAcces.Mobile.Pages;

namespace NovAcces.Mobile;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	// App agent : on affiche d'abord un écran de chargement, puis on charge la
	// config depuis le stockage sécurisé EN ASYNCHRONE (jamais en bloquant le
	// thread principal), et on route vers le poste de contrôle si le terminal est
	// enrôlé, sinon vers l'écran d'enrôlement. ScanPage n'est résolu (donc l'API
	// client et le vérificateur construits) qu'une fois la config chargée.
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new NavigationPage(new LoadingPage()));
		_ = RouteAsync(window);
		return window;
	}

	private async Task RouteAsync(Window window)
	{
		var config = _services.GetRequiredService<Services.AgentConfig>();
		try { await config.LoadFromSecureStorageAsync(); }
		catch { /* stockage indisponible : on partira sur l'enrôlement */ }

		// Terminal non enrôlé → scan du QR d'invitation. Sinon, l'agent doit PRENDRE SON POSTE
		// (matricule + PIN) avant d'accéder au scan — traçabilité individuelle.
		Page root = config.IsEnrolled
			? _services.GetRequiredService<ShiftPage>()
			: _services.GetRequiredService<EnrollmentPage>();

		await MainThread.InvokeOnMainThreadAsync(() => window.Page = new NavigationPage(root));
	}
}
