using System;
using ImGuiNET;
using System.Numerics;
using Pulsar4X.Client.Interface.Widgets;
using Pulsar4X.Api;

namespace Pulsar4X.Client
{
    public class TimeControl : UniquePulsarGuiWindow<TimeControl>
    {
        // The client reads the clock from the galaxy model and submits changes as commands; it no
        // longer touches the engine's MasterTimePulse directly.
        private TimeState? Time => _uiState.GameClient?.Galaxy.Time;

        int _timeSpanValue = 1;
        int _timeSpanType = 3;
        new ImGuiWindowFlags _flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar;

        string[] _timespanTypeSelection = new string[8]
        {
            "Milliseconds",
            "Seconds",
            "Minutes",
            "Hours",
            "Days",
            "Weeks",
            "Months",
            "Years"
        };

        bool _expanded;

        float _freqTimeSpanValue = 1f;
        int _freqSpanType = 1;

        Vector2 _iconSize = new Vector2(16, 16);
        Vector2 _windowSize = new Vector2(200, 100);
        Vector2 _windowPosition = new Vector2(0, 0);
        private DateTime _barCycleStartUtc;
        private DateTime _lastBarGameDate;
        private bool _lastBarRunning;
        private bool _syncedTickFromGalaxy;

        private TimeControl()
        {
            IsActive = true;
        }

        internal static TimeControl GetInstance()
        {
            if(_uiState.TryGetUniqueWindow<TimeControl>(out var window))
            {
                return window;
            }

            return _uiState.AddUniqueWindow(new TimeControl());
        }

        private void Submit(TimeControlRequest request) => _uiState.GameClient?.SetTimeControlAsync(request);

        internal override void Display()
        {
            if (Time is { } && !_syncedTickFromGalaxy)
            {
                _syncedTickFromGalaxy = true;
                ReadTimeSpan();
                ReadFreqency();
            }

            var time = Time;
            bool isPaused = !(time?.IsRunning ?? false);
            bool isStopping = time?.IsStopping ?? false;
            var buttonTexture = isPaused ? _uiState.Img_Play() : _uiState.Img_Pause();

            ImGui.SetNextWindowSize(_windowSize, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(_windowPosition, ImGuiCond.Appearing);

            Window.Begin("TimeControl", ref IsActive, _flags);

            DrawTickProgressBar(time);

            ImGui.PushItemWidth(100);

            // Same clock as the map: global Ticklength steps (Aurora increments).
            DateTime currenttime = _uiState.SelectedSystemTime;
            if (currenttime == default)
                currenttime = time?.GameDateTime ?? default;

            // Small arrow button for expanding time frequency menu
            if (ImGui.ArrowButton("##expand", _expanded ? ImGuiDir.Down : ImGuiDir.Right))
                _expanded = !_expanded;

            // Date display
            ImGui.SameLine();
            ImGui.Text(currenttime.ToShortDateString());

            // Time span slider
            ImGui.SameLine();
            ImGui.BeginDisabled(!isPaused);
            if (ImGui.SliderInt("##spnSldr", ref _timeSpanValue, 1, 60, _timeSpanValue.ToString()))
                AdjustTimeSpan();

            // Time duration combo
            ImGui.SameLine();
            if (ImGui.Combo("##spnCmbo", ref _timeSpanType, _timespanTypeSelection, _timespanTypeSelection.Length))
                AdjustTimeSpan();

            ImGui.EndDisabled();

            ImGui.SameLine();

            // Keep Play/Pause clickable while IsStopping — otherwise a long ProcessSystem after
            // Pause leaves the control locked and feels like a spontaneous freeze.
            if (ImGui.ImageButton("playpause", buttonTexture.ToTextureRef(), _iconSize))
            {
                PausePlayPressed();
            }
            if (isStopping && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Simulation stoppt …");

            // Step button only shown when paused
            if (isPaused)
            {
                ImGui.SameLine();
                if (ImGui.ImageButton("onestep", _uiState.Img_OneStep().ToTextureRef(), _iconSize))
                {
                    OneStepPressed();
                }
            }
            else
            {
                ImGui.SameLine();
                ImGui.InvisibleButton("##onestep_invisbtn", _iconSize);
            }

            //When the submenu is expanded allow the user to adjust time frequency
            if (_expanded)
            {
                ImGui.PushItemWidth(100);
                ImGui.Indent();
                ImGui.Text(currenttime.ToString(_uiState.GameSettings.GetTimeFormat()));

                ImGui.BeginDisabled(!isPaused);
                ImGui.SameLine();
                float freqSliderMin = _freqSpanType == 0 ? 1 : 0.001f;
                float freqSliderMax = _freqSpanType == 0 ? 1000 : 60;
                if (_freqTimeSpanValue > freqSliderMax)
                    freqSliderMax = _freqTimeSpanValue;
                if (_freqTimeSpanValue > 0 && _freqTimeSpanValue < freqSliderMin)
                    freqSliderMin = _freqTimeSpanValue;

                string freqFormat = _freqSpanType == 0 ? "%.0f" : "%.3g";
                if (ImGui.SliderFloat("##freqSldr", ref _freqTimeSpanValue, freqSliderMin, freqSliderMax, freqFormat, ImGuiSliderFlags.None))
                {
                    _freqTimeSpanValue = _freqSpanType == 0
                        ? (float)Math.Round(_freqTimeSpanValue)
                        : (float)Math.Round(_freqTimeSpanValue, 3);
                    AdjustFreqency();
                }

                ImGui.SameLine();
                if (ImGui.Combo("##freqCmbo", ref _freqSpanType, _timespanTypeSelection, _timespanTypeSelection.Length))
                    ReadFreqency();
                ImGui.EndDisabled();
            }
            Window.End();
        }

        private void DrawTickProgressBar(TimeState? time)
        {
            bool running = (time?.IsRunning ?? false) || (time?.IsStopping ?? false);
            if (!running)
            {
                _lastBarRunning = false;
                return;
            }

            if (time is { } t
                && (t.GameDateTime != _lastBarGameDate || !_lastBarRunning))
            {
                _barCycleStartUtc = DateTime.UtcNow;
                _lastBarGameDate = t.GameDateTime;
                _lastBarRunning = true;
            }

            // Approximate: fill over TickFrequency (real-time gap between ticks). If the sim is
            // slower than that, the bar sits full until the date jumps.
            double periodMs = Math.Max(50.0, time?.TickFrequency.TotalMilliseconds ?? 1000.0);
            float progress = (float)Math.Clamp(
                (DateTime.UtcNow - _barCycleStartUtc).TotalMilliseconds / periodMs, 0.0, 1.0);

            var drawList = ImGui.GetWindowDrawList();
            var winPos = ImGui.GetWindowPos();
            var winSize = ImGui.GetWindowSize();
            const float barHeight = 3f;

            drawList.AddRectFilled(
                winPos,
                new Vector2(winPos.X + winSize.X, winPos.Y + barHeight),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.18f, 0.22f, 0.9f)));

            if (progress > 0f)
            {
                drawList.AddRectFilled(
                    winPos,
                    new Vector2(winPos.X + winSize.X * progress, winPos.Y + barHeight),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.55f, 0.95f, 1f)));
            }

            if (ImGui.IsMouseHoveringRect(winPos, new Vector2(winPos.X + winSize.X, winPos.Y + barHeight)))
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Nächster Tick (ungefähr)");
                if (time is { } tt && tt.TickLength > TimeSpan.Zero)
                    ImGui.TextUnformatted($"Tick: {FormatTickLength(tt.TickLength)}");
                ImGui.EndTooltip();
            }
        }

        private static string FormatTickLength(TimeSpan span)
        {
            if (span.TotalDays >= 365) return (span.TotalDays / 365.0).ToString("0.##") + " years";
            if (span.TotalDays >= 30) return (span.TotalDays / 30.0).ToString("0.##") + " months";
            if (span.TotalDays >= 7) return (span.TotalDays / 7.0).ToString("0.##") + " weeks";
            if (span.TotalDays >= 1) return span.TotalDays.ToString("0.##") + " days";
            if (span.TotalHours >= 1) return span.TotalHours.ToString("0.##") + " hours";
            if (span.TotalMinutes >= 1) return span.TotalMinutes.ToString("0.##") + " minutes";
            return span.TotalSeconds.ToString("0.##") + " seconds";
        }

        // Converts a (value, unit-index) pair from the combo boxes into a TimeSpan.
        private static TimeSpan ToTimeSpan(double value, int unitType) => unitType switch
        {
            0 => TimeSpan.FromMilliseconds(value),
            1 => TimeSpan.FromSeconds(value),
            2 => TimeSpan.FromMinutes(value),
            3 => TimeSpan.FromHours(value),
            4 => TimeSpan.FromDays(value),
            5 => TimeSpan.FromDays(value * 7),
            6 => TimeSpan.FromDays(value * 30),
            7 => TimeSpan.FromDays(value * 365),
            _ => TimeSpan.FromHours(value),
        };

        void AdjustTimeSpan()
        {
            Submit(new TimeControlRequest(TimeControlAction.SetTickLength, TickLength: ToTimeSpan(_timeSpanValue, _timeSpanType)));
        }

        void ReadTimeSpan()
        {
            if (Time is not { } time) return;

            _timeSpanValue = _timeSpanType switch
            {
                0 => (int)time.TickLength.TotalMilliseconds,
                1 => (int)time.TickLength.TotalSeconds,
                2 => (int)time.TickLength.TotalMinutes,
                3 => (int)time.TickLength.TotalHours,
                4 => (int)time.TickLength.TotalDays,
                5 => (int)time.TickLength.TotalDays / 7,
                6 => (int)time.TickLength.TotalDays / 30,
                7 => (int)time.TickLength.TotalDays / 365,
                _ => _timeSpanValue,
            };
        }

        void AdjustFreqency()
        {
            Submit(new TimeControlRequest(TimeControlAction.SetTickFrequency, TickFrequency: ToTimeSpan(_freqTimeSpanValue, _freqSpanType)));
        }

        void ReadFreqency()
        {
            if (Time is not { } time) return;

            _freqTimeSpanValue = _freqSpanType switch
            {
                0 => (float)time.TickFrequency.TotalMilliseconds,
                1 => (float)time.TickFrequency.TotalSeconds,
                2 => (float)time.TickFrequency.TotalMinutes,
                3 => (float)time.TickFrequency.TotalHours,
                4 => (float)time.TickFrequency.TotalDays,
                5 => (float)time.TickFrequency.TotalDays / 7,
                6 => (float)time.TickFrequency.TotalDays / 30,
                7 => (float)time.TickFrequency.TotalDays / 365,
                _ => _freqTimeSpanValue,
            };
        }

        internal void PausePlayPressed()
        {
            bool isRunning = Time?.IsRunning ?? false;
            Submit(new TimeControlRequest(isRunning ? TimeControlAction.Pause : TimeControlAction.Start));
        }

        internal void OneStepPressed()
        {
            // Advances by the current tick length (set via the time-span controls).
            Submit(new TimeControlRequest(TimeControlAction.StepOnce));
        }
    }
}
