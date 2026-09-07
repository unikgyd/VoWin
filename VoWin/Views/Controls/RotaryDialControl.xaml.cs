using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VoWin.Services;

namespace VoWin.Views.Controls
{
    public partial class RotaryDialControl : UserControl
    {
        public static readonly DependencyProperty DigitDialedCommandProperty =
            DependencyProperty.Register(
                nameof(DigitDialedCommand),
                typeof(ICommand),
                typeof(RotaryDialControl),
                new PropertyMetadata(null));

        public ICommand? DigitDialedCommand
        {
            get => (ICommand?)GetValue(DigitDialedCommandProperty);
            set => SetValue(DigitDialedCommandProperty, value);
        }

        public event EventHandler<char>? DigitDialed;

        private bool _isDragging;
        private bool _isReturning;
        private bool _hasMoved;
        private bool _reachedStop;
        private char _activeDigit = '1';
        private double _targetAngle = 72.0;
        private double _lastMouseAngle;
        private double _accumulatedRotation;
        private Point _mouseDownPos;

        public RotaryDialControl()
        {
            InitializeComponent();
            MouseMove += OnControlMouseMove;
            MouseLeftButtonUp += OnControlMouseUp;
        }

        private static double GetRotationAngleForDigit(char digit)
        {
            return digit switch
            {
                '1' => 72.0,
                '2' => 97.0,
                '3' => 122.0,
                '4' => 147.0,
                '5' => 172.0,
                '6' => 197.0,
                '7' => 222.0,
                '8' => 247.0,
                '9' => 272.0,
                '0' => 297.0,
                _ => 72.0
            };
        }

        private static double GetAngleFromCenter(Point pt)
        {
            // Center is at (140, 140)
            double dx = pt.X - 140.0;
            double dy = pt.Y - 140.0;

            // 0 deg at top (12 o'clock), clockwise positive [0, 360)
            double rad = Math.Atan2(dx, -dy);
            double deg = rad * (180.0 / Math.PI);
            if (deg < 0) deg += 360.0;
            return deg;
        }

        private void ShowCenterDigit(char digit)
        {
            TxtActiveDigit.Text = digit.ToString();
            DefaultHubView.Visibility = Visibility.Collapsed;
            ActiveDigitHubView.Visibility = Visibility.Visible;
        }

        private void HideCenterDigit()
        {
            if (_isDragging || _isReturning) return;
            DefaultHubView.Visibility = Visibility.Visible;
            ActiveDigitHubView.Visibility = Visibility.Collapsed;
        }

        private void OnHoleMouseEnter(object sender, MouseEventArgs e)
        {
            if (_isDragging || _isReturning) return;
            if (sender is FrameworkElement elem && elem.Tag is string s && s.Length > 0)
            {
                ShowCenterDigit(s[0]);
            }
        }

        private void OnHoleMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isDragging || _isReturning) return;
            HideCenterDigit();
        }

        private void OnHoleMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isReturning || _isDragging) return;

            if (sender is FrameworkElement elem && elem.Tag is string s && s.Length > 0)
            {
                _activeDigit = s[0];
            }
            else
            {
                _activeDigit = '1';
            }

            _targetAngle = GetRotationAngleForDigit(_activeDigit);
            _mouseDownPos = e.GetPosition(this);
            _lastMouseAngle = GetAngleFromCenter(_mouseDownPos);
            _accumulatedRotation = 0.0;
            _isDragging = true;
            _hasMoved = false;
            _reachedStop = false;

            ShowCenterDigit(_activeDigit);
            CaptureMouse();
            e.Handled = true;
        }

        private void OnControlMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _isReturning) return;

            var mousePt = e.GetPosition(this);
            double dx = mousePt.X - 140.0;
            double dy = mousePt.Y - 140.0;

            // Dead zone near center to prevent jumpy angles
            if (dx * dx + dy * dy < 32 * 32) return;

            // Check if user has moved noticeably from down position
            double moveDistSq = (mousePt.X - _mouseDownPos.X) * (mousePt.X - _mouseDownPos.X) +
                               (mousePt.Y - _mouseDownPos.Y) * (mousePt.Y - _mouseDownPos.Y);
            if (moveDistSq > 36) // > 6px
            {
                _hasMoved = true;
            }

            var currentMouseAngle = GetAngleFromCenter(mousePt);
            var diff = currentMouseAngle - _lastMouseAngle;

            // Handle angular wrap-around seamlessly
            if (diff < -180.0) diff += 360.0;
            else if (diff > 180.0) diff -= 360.0;

            _lastMouseAngle = currentMouseAngle;

            // Accumulate clockwise rotation
            _accumulatedRotation = Math.Clamp(_accumulatedRotation + diff, 0.0, _targetAngle);
            RotorTransform.Angle = _accumulatedRotation;

            if (!_reachedStop && _accumulatedRotation >= _targetAngle - 2.0)
            {
                _reachedStop = true;
                SoundEffectService.Instance.PlayDtmfTone(_activeDigit);
            }
        }

        private void OnControlMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;

            _isDragging = false;
            ReleaseMouseCapture();

            if (!_hasMoved)
            {
                // User simply clicked the hole: instant smooth auto-spin to stop and back
                RunAutoSpinAnimation(_activeDigit, _targetAngle);
            }
            else
            {
                // Dragged release: check if reached near stop
                bool confirmed = _reachedStop || _accumulatedRotation >= (_targetAngle * 0.65);
                if (confirmed)
                {
                    ConfirmDigit(_activeDigit);
                }
                SpringBack(RotorTransform.Angle);
            }
        }

        private void RunAutoSpinAnimation(char digit, double targetAngle)
        {
            _isReturning = true;
            ShowCenterDigit(digit);

            var windDuration = TimeSpan.FromMilliseconds(130 + (targetAngle / 300.0) * 100);

            var animForward = new DoubleAnimation
            {
                From = 0.0,
                To = targetAngle,
                Duration = windDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            animForward.Completed += (s, ev) =>
            {
                ConfirmDigit(digit);
                SpringBack(targetAngle);
            };

            RotorTransform.BeginAnimation(RotateTransform.AngleProperty, animForward);
        }

        private void SpringBack(double fromAngle)
        {
            _isReturning = true;
            var returnDuration = TimeSpan.FromMilliseconds(180 + (fromAngle / 300.0) * 200);

            var animReturn = new DoubleAnimation
            {
                From = fromAngle,
                To = 0.0,
                Duration = returnDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            animReturn.Completed += (s, ev) =>
            {
                RotorTransform.BeginAnimation(RotateTransform.AngleProperty, null);
                RotorTransform.Angle = 0;
                _isReturning = false;
                _reachedStop = false;
                _accumulatedRotation = 0;
                HideCenterDigit();
            };

            RotorTransform.BeginAnimation(RotateTransform.AngleProperty, animReturn);
        }

        private void ConfirmDigit(char digit)
        {
            SoundEffectService.Instance.PlayDtmfTone(digit);
            DigitDialed?.Invoke(this, digit);

            if (DigitDialedCommand?.CanExecute(digit.ToString()) == true)
            {
                DigitDialedCommand.Execute(digit.ToString());
            }
        }
    }
}
