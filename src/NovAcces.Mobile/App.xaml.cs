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

	// App agent : poste de contrôle si le terminal est enrôlé, sinon écran
	// d'enrôlement. Tant que la config est vide, on ne résout PAS ScanPage (donc
	// ni l'API client ni le vérificateur) — évite de les construire sans config.
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var config = _services.GetRequiredService<Services.AgentConfig>();
		Page root = config.IsEnrolled
			? _services.GetRequiredService<ScanPage>()
			: _services.GetRequiredService<EnrollmentPage>();
		return new Window(new NavigationPage(root));
	}
}
