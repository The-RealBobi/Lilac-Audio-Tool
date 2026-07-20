using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Level5.AudioTool.Gui;

public sealed class PlaybackTimeline : Control
{
    public const double RulerHeight = 24;

    public static readonly StyledProperty<int> TotalSamplesProperty =
        AvaloniaProperty.Register<PlaybackTimeline, int>(nameof(TotalSamples));

    public static readonly StyledProperty<int> PlayheadSampleProperty =
        AvaloniaProperty.Register<PlaybackTimeline, int>(nameof(PlayheadSample));

    public static readonly StyledProperty<int> LoopStartProperty =
        AvaloniaProperty.Register<PlaybackTimeline, int>(nameof(LoopStart));

    public static readonly StyledProperty<int> LoopEndProperty =
        AvaloniaProperty.Register<PlaybackTimeline, int>(nameof(LoopEnd));

    public static readonly StyledProperty<bool> HasLoopProperty =
        AvaloniaProperty.Register<PlaybackTimeline, bool>(nameof(HasLoop));

    public static readonly StyledProperty<double[]> PeaksProperty =
        AvaloniaProperty.Register<PlaybackTimeline, double[]>(nameof(Peaks), Array.Empty<double>());

    private readonly Pen _gridPen = new(new SolidColorBrush(Color.FromRgb(44, 48, 52)), 1);
    private readonly Pen _majorGridPen = new(new SolidColorBrush(Color.FromRgb(22, 25, 28)), 1);
    private readonly Pen _wavePen = new(new SolidColorBrush(Color.FromRgb(25, 31, 38)), 1);
    private readonly Pen _playheadPen = new(Brushes.White, 2);
    private readonly Typeface _typeface = new("Inter");

    public int TotalSamples
    {
        get => GetValue(TotalSamplesProperty);
        set => SetValue(TotalSamplesProperty, value);
    }

    public int PlayheadSample
    {
        get => GetValue(PlayheadSampleProperty);
        set => SetValue(PlayheadSampleProperty, value);
    }

    public int LoopStart
    {
        get => GetValue(LoopStartProperty);
        set => SetValue(LoopStartProperty, value);
    }

    public int LoopEnd
    {
        get => GetValue(LoopEndProperty);
        set => SetValue(LoopEndProperty, value);
    }

    public bool HasLoop
    {
        get => GetValue(HasLoopProperty);
        set => SetValue(HasLoopProperty, value);
    }

    public double[] Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public PlaybackTimeline()
    {
        ClipToBounds = true;
    }

    static PlaybackTimeline()
    {
        AffectsRender<PlaybackTimeline>(
            TotalSamplesProperty,
            PlayheadSampleProperty,
            LoopStartProperty,
            LoopEndProperty,
            HasLoopProperty,
            PeaksProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        var width = bounds.Width;
        var height = bounds.Height;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var rulerHeight = RulerHeight;
        var clipTop = rulerHeight + 2;
        var clipHeight = Math.Max(16, height - clipTop - 2);
        var clipRect = new Rect(0.5, clipTop, Math.Max(0, width - 1), clipHeight);

        context.FillRectangle(new SolidColorBrush(Color.FromRgb(74, 74, 74)), new Rect(0, 0, width, rulerHeight));
        DrawRuler(context, width, rulerHeight, height);

        context.FillRectangle(new SolidColorBrush(Color.FromRgb(83, 139, 226)), clipRect);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(20, 36, 58)), 1), clipRect);

        DrawWaveform(context, clipRect);

        if (TotalSamples > 0)
        {
            var playheadX = SampleToX(PlayheadSample, width);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(76, 255, 255, 255)), new Rect(0, clipTop, playheadX, clipHeight));
            context.DrawLine(_playheadPen, new Point(playheadX, 0), new Point(playheadX, height));

            if (HasLoop && LoopEnd > LoopStart)
            {
                DrawLoopRegion(context, width, rulerHeight, clipTop, clipHeight);
            }
        }

        if (TotalSamples <= 0)
        {
            var text = new FormattedText(
                "Sin audio cargado",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _typeface,
                12,
                new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)));
            context.DrawText(text, new Point(10, clipTop + clipHeight / 2 - text.Height / 2));
        }
    }

    public int XToSample(double x)
    {
        if (TotalSamples <= 0 || Bounds.Width <= 0)
        {
            return 0;
        }

        return (int)Math.Round(Math.Clamp(x, 0, Bounds.Width) / Bounds.Width * TotalSamples);
    }

    public double SampleToX(int sample)
    {
        return SampleToX(sample, Bounds.Width);
    }

    private void DrawRuler(DrawingContext context, double width, double rulerHeight, double height)
    {
        const int majorDivisions = 4;
        var minorStep = Math.Max(6, width / 96);
        for (var x = 0.0; x <= width; x += minorStep)
        {
            context.DrawLine(_gridPen, new Point(x, rulerHeight - 5), new Point(x, rulerHeight));
        }

        for (var i = 0; i <= majorDivisions; i++)
        {
            var x = width * i / majorDivisions;
            context.DrawLine(_majorGridPen, new Point(x, 0), new Point(x, height));
            var label = TotalSamples <= 0 ? i.ToString() : Math.Round(TotalSamples * i / (double)majorDivisions).ToString("0");
            var text = new FormattedText(
                label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                10,
                Brushes.Black);
            context.DrawText(text, new Point(Math.Clamp(x + 3, 0, Math.Max(0, width - text.Width - 2)), 0));
        }
    }

    private void DrawWaveform(DrawingContext context, Rect clipRect)
    {
        var peaks = Peaks;
        var centerY = clipRect.Center.Y;
        var maxHalfHeight = Math.Max(3, clipRect.Height * 0.38);
        if (peaks.Length == 0)
        {
            context.DrawLine(_wavePen, new Point(clipRect.Left, centerY), new Point(clipRect.Right, centerY));
            return;
        }

        var columns = Math.Max(1, (int)Math.Floor(clipRect.Width));
        for (var i = 0; i < columns; i++)
        {
            var peakIndex = Math.Clamp((int)Math.Floor(i / Math.Max(1, clipRect.Width) * peaks.Length), 0, peaks.Length - 1);
            var amplitude = Math.Clamp(peaks[peakIndex], 0, 1);
            var halfHeight = Math.Max(1, amplitude * maxHalfHeight);
            var x = clipRect.Left + i + 0.5;
            context.DrawLine(_wavePen, new Point(x, centerY - halfHeight), new Point(x, centerY + halfHeight));
        }
    }

    private void DrawLoopRegion(DrawingContext context, double width, double rulerHeight, double clipTop, double clipHeight)
    {
        var startX = SampleToX(LoopStart, width);
        var endX = SampleToX(LoopEnd, width);
        var rect = new Rect(startX, clipTop, Math.Max(1, endX - startX), clipHeight);

        context.FillRectangle(new SolidColorBrush(Color.FromArgb(72, 89, 201, 242)), rect);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(84, 193, 238)), 1), rect);

        DrawLoopHandle(context, startX, rulerHeight, true);
        DrawLoopHandle(context, endX, rulerHeight, false);
    }

    private static void DrawLoopHandle(DrawingContext context, double x, double rulerHeight, bool start)
    {
        var brush = new SolidColorBrush(Color.FromRgb(84, 193, 238));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (start)
            {
                ctx.BeginFigure(new Point(x, rulerHeight + 1), true);
                ctx.LineTo(new Point(x + 18, rulerHeight + 1));
                ctx.LineTo(new Point(x + 10, rulerHeight + 9));
            }
            else
            {
                ctx.BeginFigure(new Point(x, rulerHeight + 1), true);
                ctx.LineTo(new Point(x - 18, rulerHeight + 1));
                ctx.LineTo(new Point(x - 10, rulerHeight + 9));
            }
            ctx.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
        context.DrawLine(new Pen(brush, 1), new Point(x, rulerHeight), new Point(x, 1000));
    }

    private double SampleToX(int sample, double width)
    {
        if (TotalSamples <= 0 || width <= 0)
        {
            return 0;
        }

        return Math.Clamp(sample, 0, TotalSamples) / (double)TotalSamples * width;
    }
}
