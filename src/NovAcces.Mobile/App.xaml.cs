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

	// App agent : on ouvre directement le poste de contrôle (scan caméra).
	protected override Window CreateWindow(IActivationState? activationState)
		=> new Window(new NavigationPage(_services.GetRequiredService<ScanPage>()));
}
