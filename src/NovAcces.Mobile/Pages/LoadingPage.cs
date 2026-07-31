namespace NovAcces.Mobile.Pages;

/// <summary>
/// Écran de démarrage affiché pendant le chargement asynchrone de la config
/// (stockage sécurisé). Évite tout blocage du thread principal au lancement.
/// </summary>
public sealed class LoadingPage : ContentPage
{
    public LoadingPage()
    {
        BackgroundColor = Color.FromArgb("#0E2A3A"); // navy de marque
        Content = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Spacing = 18,
            Children =
            {
                new Label
                {
                    Text = "NovAccès",
                    TextColor = Colors.White,
                    FontSize = 26,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalTextAlignment = TextAlignment.Center,
                },
                new ActivityIndicator
                {
                    IsRunning = true,
                    Color = Color.FromArgb("#F5A300"), // ambre de marque
                    HorizontalOptions = LayoutOptions.Center,
                },
            },
        };
    }
}
