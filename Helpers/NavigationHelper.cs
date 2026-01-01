using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;

namespace LinuxGate.Helpers
{
    public static class NavigationHelper
    {
        public static void NavigateWithAnimation(
            NavigationService navigation,
            Page newPage,
            TimeSpan duration,
            double slideOffset = 100,
            bool slideLeft = true)
        {
            // Ensure background color consistency
            if (newPage.Content is Grid grid)
            {
                grid.Background = Brushes.Transparent;
            }

            // Create animations
            var fadeOut = CreateFadeAnimation(1.0, 0.0, duration);
            var slideOut = CreateSlideAnimation(
                new Thickness(0),
                new Thickness(slideLeft ? -slideOffset : slideOffset, 0, 0, 0),
                duration);

            fadeOut.Completed += (s, _) =>
            {
                navigation.Navigate(newPage);

                var fadeIn = CreateFadeAnimation(0.0, 1.0, duration);
                var slideIn = CreateSlideAnimation(
                    new Thickness(slideLeft ? slideOffset : -slideOffset, 0, 0, 0),
                    new Thickness(0),
                    duration);

                newPage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                newPage.BeginAnimation(FrameworkElement.MarginProperty, slideIn);
            };

            // Start exit animations on current page
            if (navigation.Content is UIElement currentPage)
            {
                currentPage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                currentPage.BeginAnimation(FrameworkElement.MarginProperty, slideOut);
            }
        }

        public static void NavigateWithAnimationInFrame(
            Frame frame,
            Page newPage,
            TimeSpan duration,
            double slideOffset = 100,
            bool slideLeft = true)
        {
            if (newPage.Content is Grid grid)
            {
                grid.Background = Brushes.Transparent;
            }

            var fadeOut = CreateFadeAnimation(1.0, 0.0, duration);
            var slideOut = CreateSlideAnimation(
                new Thickness(0),
                new Thickness(slideLeft ? -slideOffset : slideOffset, 0, 0, 0),
                duration);

            fadeOut.Completed += (s, _) =>
            {
                frame.Navigate(newPage);

                var fadeIn = CreateFadeAnimation(0.0, 1.0, duration);
                var slideIn = CreateSlideAnimation(
                    new Thickness(slideLeft ? slideOffset : -slideOffset, 0, 0, 0),
                    new Thickness(0),
                    duration);

                newPage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                newPage.BeginAnimation(FrameworkElement.MarginProperty, slideIn);
            };

            if (frame.Content is UIElement currentPage)
            {
                currentPage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                currentPage.BeginAnimation(FrameworkElement.MarginProperty, slideOut);
            }
        }

        private static DoubleAnimation CreateFadeAnimation(double from, double to, TimeSpan duration)
        {
            return new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
        }

        private static ThicknessAnimation CreateSlideAnimation(Thickness from, Thickness to, TimeSpan duration)
        {
            return new ThicknessAnimation
            {
                From = from,
                To = to,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
        }
    }
}
