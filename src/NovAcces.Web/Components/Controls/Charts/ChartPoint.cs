namespace NovAcces.Web.Components.Controls.Charts;

/// <summary>Point de donnée générique pour BarChart/StackedBarChart/DonutChart — un label, une valeur, une couleur optionnelle (sinon dérivée de la palette de la marque).</summary>
public sealed record ChartPoint(string Label, double Value, string? Color = null, string? Tooltip = null);

/// <summary>Une série nommée (une couleur), pour StackedBarChart — plusieurs valeurs par catégorie (ex. Entrées/Sorties/Refus par jour).</summary>
public sealed record ChartSeries(string Name, string Color, IReadOnlyList<double> Values);
