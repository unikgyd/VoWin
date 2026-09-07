using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace VoWin.Views.Controls
{
    public partial class VoWifiStageRing : UserControl
    {
        private static readonly Brush DefaultRedBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        private static readonly Brush DefaultStage1Brush = new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
        private static readonly Brush DefaultStage2Brush = new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08));
        private static readonly Brush DefaultStage3Brush = new SolidColorBrush(Color.FromRgb(0x84, 0xCC, 0x16));
        private static readonly Brush DefaultStage4Brush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        private static readonly Brush DefaultGrayBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

        static VoWifiStageRing()
        {
            DefaultRedBrush.Freeze();
            DefaultStage1Brush.Freeze();
            DefaultStage2Brush.Freeze();
            DefaultStage3Brush.Freeze();
            DefaultStage4Brush.Freeze();
            DefaultGrayBrush.Freeze();
        }

        public static readonly DependencyProperty Stage1SuccessProperty =
            DependencyProperty.Register(nameof(Stage1Success), typeof(bool), typeof(VoWifiStageRing),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty Stage2SuccessProperty =
            DependencyProperty.Register(nameof(Stage2Success), typeof(bool), typeof(VoWifiStageRing),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty Stage3SuccessProperty =
            DependencyProperty.Register(nameof(Stage3Success), typeof(bool), typeof(VoWifiStageRing),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty Stage4SuccessProperty =
            DependencyProperty.Register(nameof(Stage4Success), typeof(bool), typeof(VoWifiStageRing),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty IsFullyRegisteredProperty =
            DependencyProperty.Register(nameof(IsFullyRegistered), typeof(bool), typeof(VoWifiStageRing),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty Stage1DetailProperty =
            DependencyProperty.Register(nameof(Stage1Detail), typeof(string), typeof(VoWifiStageRing),
                new PropertyMetadata(string.Empty, OnStateChanged));

        public static readonly DependencyProperty Stage2DetailProperty =
            DependencyProperty.Register(nameof(Stage2Detail), typeof(string), typeof(VoWifiStageRing),
                new PropertyMetadata(string.Empty, OnStateChanged));

        public static readonly DependencyProperty Stage3DetailProperty =
            DependencyProperty.Register(nameof(Stage3Detail), typeof(string), typeof(VoWifiStageRing),
                new PropertyMetadata(string.Empty, OnStateChanged));

        public static readonly DependencyProperty Stage4DetailProperty =
            DependencyProperty.Register(nameof(Stage4Detail), typeof(string), typeof(VoWifiStageRing),
                new PropertyMetadata(string.Empty, OnStateChanged));

        public static readonly DependencyProperty Stage1BrushProperty =
            DependencyProperty.Register(nameof(Stage1Brush), typeof(Brush), typeof(VoWifiStageRing),
                new PropertyMetadata(null, OnStateChanged));

        public static readonly DependencyProperty Stage2BrushProperty =
            DependencyProperty.Register(nameof(Stage2Brush), typeof(Brush), typeof(VoWifiStageRing),
                new PropertyMetadata(null, OnStateChanged));

        public static readonly DependencyProperty Stage3BrushProperty =
            DependencyProperty.Register(nameof(Stage3Brush), typeof(Brush), typeof(VoWifiStageRing),
                new PropertyMetadata(null, OnStateChanged));

        public static readonly DependencyProperty Stage4BrushProperty =
            DependencyProperty.Register(nameof(Stage4Brush), typeof(Brush), typeof(VoWifiStageRing),
                new PropertyMetadata(null, OnStateChanged));

        public static readonly DependencyProperty CenterDotBrushProperty =
            DependencyProperty.Register(nameof(CenterDotBrush), typeof(Brush), typeof(VoWifiStageRing),
                new PropertyMetadata(null, OnStateChanged));

        public static readonly DependencyProperty IsBreathingProperty =
            DependencyProperty.Register(nameof(IsBreathing), typeof(bool), typeof(VoWifiStageRing),
                new PropertyMetadata(false, OnStateChanged));

        public Brush? Stage1Brush
        {
            get => (Brush?)GetValue(Stage1BrushProperty);
            set => SetValue(Stage1BrushProperty, value);
        }

        public Brush? Stage2Brush
        {
            get => (Brush?)GetValue(Stage2BrushProperty);
            set => SetValue(Stage2BrushProperty, value);
        }

        public Brush? Stage3Brush
        {
            get => (Brush?)GetValue(Stage3BrushProperty);
            set => SetValue(Stage3BrushProperty, value);
        }

        public Brush? Stage4Brush
        {
            get => (Brush?)GetValue(Stage4BrushProperty);
            set => SetValue(Stage4BrushProperty, value);
        }

        public Brush? CenterDotBrush
        {
            get => (Brush?)GetValue(CenterDotBrushProperty);
            set => SetValue(CenterDotBrushProperty, value);
        }

        public bool IsBreathing
        {
            get => (bool)GetValue(IsBreathingProperty);
            set => SetValue(IsBreathingProperty, value);
        }

        public bool Stage1Success
        {
            get => (bool)GetValue(Stage1SuccessProperty);
            set => SetValue(Stage1SuccessProperty, value);
        }

        public bool Stage2Success
        {
            get => (bool)GetValue(Stage2SuccessProperty);
            set => SetValue(Stage2SuccessProperty, value);
        }

        public bool Stage3Success
        {
            get => (bool)GetValue(Stage3SuccessProperty);
            set => SetValue(Stage3SuccessProperty, value);
        }

        public bool Stage4Success
        {
            get => (bool)GetValue(Stage4SuccessProperty);
            set => SetValue(Stage4SuccessProperty, value);
        }

        public bool IsFullyRegistered
        {
            get => (bool)GetValue(IsFullyRegisteredProperty);
            set => SetValue(IsFullyRegisteredProperty, value);
        }

        public string Stage1Detail
        {
            get => (string)GetValue(Stage1DetailProperty);
            set => SetValue(Stage1DetailProperty, value);
        }

        public string Stage2Detail
        {
            get => (string)GetValue(Stage2DetailProperty);
            set => SetValue(Stage2DetailProperty, value);
        }

        public string Stage3Detail
        {
            get => (string)GetValue(Stage3DetailProperty);
            set => SetValue(Stage3DetailProperty, value);
        }

        public string Stage4Detail
        {
            get => (string)GetValue(Stage4DetailProperty);
            set => SetValue(Stage4DetailProperty, value);
        }

        private Storyboard? _breathingStoryboard;
        private bool _isLoaded;

        public VoWifiStageRing()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SetupInteractivity();
        }

        private void SetupInteractivity()
        {
            // Root Grid scale hover effect
            RootRingGrid.MouseEnter += (s, e) =>
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.05, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.05, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            };

            RootRingGrid.MouseLeave += (s, e) =>
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(RootScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(RootScale.ScaleY, 1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            };

            // Segment hover animations
            AttachSegmentHover(Seg1Path, Seg1Scale);
            AttachSegmentHover(Seg2Path, Seg2Scale);
            AttachSegmentHover(Seg3Path, Seg3Scale);
            AttachSegmentHover(Seg4Path, Seg4Scale);

            // Center dot hover animation
            CenterDotCore.MouseEnter += (s, e) =>
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                CenterDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.35, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                CenterDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.35, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            };

            CenterDotCore.MouseLeave += (s, e) =>
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                CenterDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(CenterDotScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                CenterDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(CenterDotScale.ScaleY, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            };
        }

        private void AttachSegmentHover(Path path, ScaleTransform scale)
        {
            path.MouseEnter += (s, e) =>
            {
                Panel.SetZIndex(path, 10);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                // Smoothly thicken stroke from 6 to 9.5
                path.BeginAnimation(Shape.StrokeThicknessProperty, new DoubleAnimation(6.0, 9.5, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });

                // Scale segment outwards slightly (1.08x)
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.08, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.08, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            };

            path.MouseLeave += (s, e) =>
            {
                Panel.SetZIndex(path, 0);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                // Restore stroke thickness back to 6.0
                path.BeginAnimation(Shape.StrokeThicknessProperty, new DoubleAnimation(path.StrokeThickness, 6.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

                // Restore scale back to 1.0
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            _breathingStoryboard = TryFindResource("BreathingAnimation") as Storyboard;
            UpdateVisuals();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            try
            {
                _breathingStoryboard?.Stop(this);
            }
            catch { }
        }

        private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is VoWifiStageRing ring)
            {
                ring.UpdateVisuals();
            }
        }

        public void UpdateVisuals()
        {
            if (!_isLoaded) return;

            // Segment 1: DNS Resolution (Top-Right)
            Seg1Path.Stroke = Stage1Brush ?? (Stage1Success ? DefaultStage1Brush : DefaultGrayBrush);
            Seg1Path.ToolTip = $"阶段 1: ePDG 核心网域名解析 (DNS FQDN)\n状态: {(Stage1Success ? "成功 (已解析)" : "未完成")}\n详情: {Stage1Detail}";

            // Segment 2: IKEv2 / EAP-AKA (Bottom-Right)
            Seg2Path.Stroke = Stage2Brush ?? (Stage2Success ? DefaultStage2Brush : DefaultGrayBrush);
            Seg2Path.ToolTip = $"阶段 2: IKEv2 隧道握手与 EAP-AKA USIM 鉴权\n状态: {(Stage2Success ? "成功 (鉴权通过)" : "未完成")}\n详情: {Stage2Detail}";

            // Segment 3: IPsec Child SA (Bottom-Left)
            Seg3Path.Stroke = Stage3Brush ?? (Stage3Success ? DefaultStage3Brush : DefaultGrayBrush);
            Seg3Path.ToolTip = $"阶段 3: IPsec 子安全关联与虚拟网络隧道\n状态: {(Stage3Success ? "成功 (隧道建立)" : "未完成")}\n详情: {Stage3Detail}";

            // Segment 4: SIP REGISTER IMS (Top-Left)
            Seg4Path.Stroke = Stage4Brush ?? (Stage4Success ? DefaultStage4Brush : DefaultGrayBrush);
            Seg4Path.ToolTip = $"阶段 4: IMS 核心网 SIP REGISTER 注册 (200 OK)\n状态: {(Stage4Success ? "成功 (已注册)" : "未完成")}\n详情: {Stage4Detail}";

            // Center Dot Logic: Breathing glow if fully registered or connecting, static otherwise
            bool fullyRegistered = IsFullyRegistered || (Stage1Success && Stage2Success && Stage3Success && Stage4Success);
            bool shouldBreathe = IsBreathing || fullyRegistered;

            Brush centerBrush = CenterDotBrush ?? (fullyRegistered ? DefaultStage4Brush : (IsBreathing ? DefaultStage1Brush : DefaultGrayBrush));
            CenterDotCore.Fill = centerBrush;
            CenterDotHalo.Fill = centerBrush;

            if (shouldBreathe)
            {
                CenterDotCore.ToolTip = fullyRegistered
                    ? "VoWiFi 核心网: 已完全就绪 (IMS Registered 200 OK)\n高清语音通信已就绪"
                    : "VoWiFi 核心网: 正在建立安全隧道与协商中...";

                try
                {
                    _breathingStoryboard?.Begin(this, isControllable: true);
                }
                catch { }
            }
            else
            {
                try
                {
                    _breathingStoryboard?.Stop(this);
                }
                catch { }

                CenterDotHalo.Opacity = 0;
                CenterDotCore.Opacity = 0.85;
                CenterDotCore.ToolTip = $"VoWiFi 核心网: 未完全就绪 (达成阶段: {(Stage1Success ? 1 : 0) + (Stage2Success ? 1 : 0) + (Stage3Success ? 1 : 0) + (Stage4Success ? 1 : 0)}/4)";
            }
        }
    }
}
