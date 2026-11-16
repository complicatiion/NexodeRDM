using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace Nexode.App.Controls;

/// <summary>
/// Data template for a tab page that gives the view its view model as a local data context instead of inheriting it.
/// When the tab host swaps pages it first clears the old presenter (data context null) – an inherited context then
/// makes every <c>$parent[UserControl].DataContext…</c> binding of the leaving page report "Value is null", and during
/// the transition the page briefly sees the other tab's view model. A local context keeps the leaving page intact
/// until it is gone. Views of the same type are reused (the new view model is set on the existing view).
/// </summary>
public sealed class StableViewTemplate : IRecyclingDataTemplate
{
    /// <summary>View model type this template applies to.</summary>
    [DataType]
    public Type? DataType { get; set; }

    /// <summary>View created for it (needs a public parameterless constructor).</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type? View { get; set; }

    public bool Match(object? data) => data is not null && DataType?.IsInstanceOfType(data) == true;

    public Control? Build(object? param) => Build(param, null);

    public Control? Build(object? data, Control? existing)
    {
        if (View is null) return null;
        var view = existing is not null && existing.GetType() == View ? existing : (Control)Activator.CreateInstance(View)!;
        view.DataContext = data;
        return view;
    }
}
