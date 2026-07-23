using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Color = Windows.UI.Color;

namespace WhichBox;

/// <summary>
/// Applies a composition opacity mask with feathered edges to a XAML element.
/// Uses two linear gradients (horizontal + vertical) combined via an intermediate
/// VisualSurface to produce uniform fade on all edges.
/// </summary>
internal sealed class CompositionMaskHelper
{
    // Feather distance for ALL edges, as a fraction of the box height. Keying
    // the feather off height (a fixed pixel distance instead) keeps the same
    // proportions at every taskbar height, and applying that one distance to
    // every edge keeps the corners symmetric: a fixed-pixel fade is a small
    // feather on a tall 200%-DPI taskbar but consumes a short 100%-DPI one, and
    // a width-based horizontal fade makes the sides feather more than the top
    // and bottom on a short box. 0.175 matches the (good-looking) 200% render.
    private const float FeatherRatio = 0.175f;

    private readonly Compositor _compositor;
    private readonly CompositionVisualSurface _contentSurface;
    private readonly CompositionVisualSurface _maskSurface;
    private readonly ContainerVisual _maskContainer;
    private readonly SpriteVisual _maskedVisual;
    private readonly CompositionLinearGradientBrush _hGradient;
    private readonly CompositionLinearGradientBrush _vGradient;

    private CompositionMaskHelper(
        Compositor compositor,
        CompositionVisualSurface contentSurface,
        CompositionVisualSurface maskSurface,
        ContainerVisual maskContainer,
        SpriteVisual maskedVisual,
        CompositionLinearGradientBrush hGradient,
        CompositionLinearGradientBrush vGradient)
    {
        _compositor = compositor;
        _contentSurface = contentSurface;
        _maskSurface = maskSurface;
        _maskContainer = maskContainer;
        _maskedVisual = maskedVisual;
        _hGradient = hGradient;
        _vGradient = vGradient;
    }

    /// <summary>
    /// Sets up a composition opacity mask on the given elements.
    /// Hides <paramref name="contentContainer"/> and renders the masked output
    /// as a child visual of <paramref name="maskHost"/>.
    /// </summary>
    /// <param name="root">The root element used to obtain the Compositor and track size changes.</param>
    /// <param name="contentSource">The element whose visual content is captured and masked.</param>
    /// <param name="contentContainer">The container to hide (must wrap contentSource so VisualSurface can still capture).</param>
    /// <param name="maskHost">The element that receives the masked output visual.</param>
    public static CompositionMaskHelper Apply(
        FrameworkElement root,
        FrameworkElement contentSource,
        FrameworkElement contentContainer,
        FrameworkElement maskHost)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(root).Compositor;
        var contentVisual = ElementCompositionPreview.GetElementVisual(contentSource);

        // Capture content into a visual surface
        var contentSurface = compositor.CreateVisualSurface();
        contentSurface.SourceVisual = contentVisual;

        var contentBrush = compositor.CreateSurfaceBrush(contentSurface);
        contentBrush.HorizontalAlignmentRatio = 0;
        contentBrush.VerticalAlignmentRatio = 0;
        contentBrush.Stretch = CompositionStretch.None;

        // Horizontal gradient: transparent -> white -> white -> transparent
        var hGradient = compositor.CreateLinearGradientBrush();
        hGradient.MappingMode = CompositionMappingMode.Relative;
        hGradient.StartPoint = new Vector2(0, 0.5f);
        hGradient.EndPoint = new Vector2(1, 0.5f);
        hGradient.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(0, 255, 255, 255)));
        hGradient.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(255, 255, 255, 255)));
        hGradient.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(255, 255, 255, 255)));
        hGradient.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));

        // Vertical gradient: transparent -> white -> white -> transparent
        var vGradient = compositor.CreateLinearGradientBrush();
        vGradient.MappingMode = CompositionMappingMode.Relative;
        vGradient.StartPoint = new Vector2(0.5f, 0);
        vGradient.EndPoint = new Vector2(0.5f, 1);
        vGradient.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(0, 255, 255, 255)));
        vGradient.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(255, 255, 255, 255)));
        vGradient.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(255, 255, 255, 255)));
        vGradient.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));

        // Combine H and V into a single mask by rendering H masked by V
        // onto an intermediate visual, then capturing via VisualSurface.
        // (CompositionMaskBrush cannot be nested as Source/Mask of another.)
        var hvMask = compositor.CreateMaskBrush();
        hvMask.Source = hGradient;
        hvMask.Mask = vGradient;

        var maskVisual = compositor.CreateSpriteVisual();
        maskVisual.Brush = hvMask;
        maskVisual.RelativeSizeAdjustment = Vector2.One;

        var maskContainer = compositor.CreateContainerVisual();
        maskContainer.Children.InsertAtTop(maskVisual);

        var maskSurface = compositor.CreateVisualSurface();
        maskSurface.SourceVisual = maskContainer;

        var maskSurfaceBrush = compositor.CreateSurfaceBrush(maskSurface);
        maskSurfaceBrush.HorizontalAlignmentRatio = 0;
        maskSurfaceBrush.VerticalAlignmentRatio = 0;
        maskSurfaceBrush.Stretch = CompositionStretch.None;

        // Apply the flattened mask to the content
        var finalBrush = compositor.CreateMaskBrush();
        finalBrush.Source = contentBrush;
        finalBrush.Mask = maskSurfaceBrush;

        // Create the output visual
        var maskedVisual = compositor.CreateSpriteVisual();
        maskedVisual.Brush = finalBrush;
        maskedVisual.RelativeSizeAdjustment = Vector2.One;

        // Hide the content container (not the source visual itself, so
        // VisualSurface can still capture from it) and show the masked version.
        ElementCompositionPreview.GetElementVisual(contentContainer).IsVisible = false;
        ElementCompositionPreview.SetElementChildVisual(maskHost, maskedVisual);

        var helper = new CompositionMaskHelper(
            compositor, contentSurface, maskSurface, maskContainer,
            maskedVisual, hGradient, vGradient);

        // Keep surface size in sync
        root.SizeChanged += (_, _) => helper.UpdateSize(root);
        helper.UpdateSize(root);

        return helper;
    }

    /// <summary>
    /// Updates the mask and content surface sizes to match the root element,
    /// and recalculates gradient stop positions for uniform fade.
    /// </summary>
    public void UpdateSize(FrameworkElement root)
    {
        var w = (float)root.ActualWidth;
        var h = (float)root.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var size = new Vector2(w, h);
        _contentSurface.SourceSize = size;
        _maskSurface.SourceSize = size;
        _maskContainer.Size = size;

        // Feather every edge by the same distance (a fraction of height) so the
        // corners are symmetric and the box looks identical at every DPI. As a
        // fraction of each axis that distance is featherPx/w horizontally and
        // FeatherRatio vertically.
        var featherPx = FeatherRatio * h;
        var hRatio = Math.Min(featherPx / w, 0.5f);
        _hGradient.ColorStops[0].Offset = 0f;
        _hGradient.ColorStops[1].Offset = hRatio;
        _hGradient.ColorStops[2].Offset = 1f - hRatio;
        _hGradient.ColorStops[3].Offset = 1f;

        var vRatio = FeatherRatio;
        _vGradient.ColorStops[0].Offset = 0f;
        _vGradient.ColorStops[1].Offset = vRatio;
        _vGradient.ColorStops[2].Offset = 1f - vRatio;
        _vGradient.ColorStops[3].Offset = 1f;

        Logger.Info($"MaskUpdateSize: w={w:0.#} h={h:0.#} featherPx={featherPx:0.#} hRatio={hRatio:0.###} vRatio={vRatio:0.###}");
    }
}
