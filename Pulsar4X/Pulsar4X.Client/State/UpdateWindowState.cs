using System;

namespace Pulsar4X.Client
{
    public abstract class UpdateWindowState
    {
        internal static GlobalUIState? _uiState;

        public abstract bool GetActive();

        public virtual void OnSystemTickChange(DateTime newDate) { }

        protected UpdateWindowState()
        {
            (_uiState ?? throw new InvalidOperationException("Global UI state is not initialized.")).UpdateableWindows.Add(this);
        }

        public void Deconstructor()
        {
            (_uiState ?? throw new InvalidOperationException("Global UI state is not initialized.")).UpdateableWindows.Remove(this);
        }

    }
}