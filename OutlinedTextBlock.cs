using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AiAssistant
{
    /// <summary>
    /// Renders text as a Path geometry so it can have a visible Stroke
    /// (stroke-only / filled / both), matching CSS -webkit-text-stroke.
    /// Drop the .cs file next to MainWindow.xaml.cs — no XAML needed.
    /// Usage in XAML: <local:OutlinedTextBlock Text="ნელი"
    ///                    Fill="Transparent" Stroke="#99FFFFFF" StrokeThickness="1"/>
    /// </summary>
    public class OutlinedTextBlock : FrameworkElement
    {
        // ── Dependency properties ─────────────────────────────────────────
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(string.Empty,
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(14.0,
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily), typeof(FontFamily), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(new FontFamily("Arial"),
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(FontWeights.Black,
                    FrameworkPropertyMetadataOptions.AffectsRender |
                    FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(Brushes.Transparent,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(Brushes.White,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
                new FrameworkPropertyMetadata(1.0,
                    FrameworkPropertyMetadataOptions.AffectsRender));

        // ── CLR wrappers ─────────────────────────────────────────────────
        public string      Text            { get => (string)GetValue(TextProperty);           set => SetValue(TextProperty,           value); }
        public double      FontSize        { get => (double)GetValue(FontSizeProperty);       set => SetValue(FontSizeProperty,       value); }
        public FontFamily  FontFamily      { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty,    value); }
        public FontWeight  FontWeight      { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty,    value); }
        public Brush       Fill            { get => (Brush)GetValue(FillProperty);            set => SetValue(FillProperty,          value); }
        public Brush       Stroke          { get => (Brush)GetValue(StrokeProperty);          set => SetValue(StrokeProperty,        value); }
        public double      StrokeThickness { get => (double)GetValue(StrokeThicknessProperty);set => SetValue(StrokeThicknessProperty, value); }

        // ── Geometry helpers ──────────────────────────────────────────────
        private Typeface GetTypeface() =>
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);

        private FormattedText GetFT() =>
            new FormattedText(
                Text ?? string.Empty,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                GetTypeface(),
                FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override Size MeasureOverride(Size available)
        {
            var ft = GetFT();
            return new Size(ft.Width + StrokeThickness, ft.Height + StrokeThickness);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var geo = GetFT().BuildGeometry(new Point(StrokeThickness / 2, StrokeThickness / 2));
            var pen = (Stroke != null && StrokeThickness > 0)
                ? new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round }
                : null;
            dc.DrawGeometry(Fill, pen, geo);
        }
    }
}