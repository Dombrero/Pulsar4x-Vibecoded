using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Pulsar4X.Datablobs;

namespace Pulsar4X.Energy
{
    /// <summary>
    /// Colony-side energy store and grid state. Batteries set capacity; plants fill stored energy;
    /// demand (e.g. AutoMines) drains it and drives <see cref="PowerEfficiency"/>.
    /// </summary>
    public class ColonyPowerDB : BaseDataBlob
    {
        public const string EnergyTypeId = "electricity";
        public const double ReserveFraction = 0.20;
        /// <summary>Baseline colony port feed in kW (ships still capped by their battery accept rate).</summary>
        public const double PortDockBonusKW = 500.0;

        /// <summary>
        /// Rolling history length (one sample per hourly power tick).
        /// 87600 h ≈ 10 years — UI can window a shorter view over the same buffer.
        /// </summary>
        public const int HistogramSize = 87600;

        /// <summary>Battery capacity in kJ.</summary>
        [JsonProperty]
        public double StorageCapacityKJ { get; set; }

        /// <summary>Stored energy in kJ.</summary>
        [JsonProperty]
        public double EnergyStoredKJ { get; set; }

        /// <summary>Last computed generation (kW).</summary>
        [JsonProperty]
        public double GenerationKW { get; set; }

        /// <summary>Last computed demand (kW).</summary>
        [JsonProperty]
        public double DemandKW { get; set; }

        /// <summary>0–1 multiplier for power-gated production (AutoMines etc.).</summary>
        [JsonProperty]
        public double PowerEfficiency { get; set; } = 1.0;

        /// <summary>Sum of colony battery charge rates + port bonus (kW).</summary>
        [JsonProperty]
        public double DockChargeRateKW { get; set; }

        /// <summary>Sum of colony battery MaxChargeRate only (kW), used to build DockChargeRate.</summary>
        [JsonProperty]
        public double BatteryChargeRateKW { get; set; }

        [JsonProperty]
        public DateTime LastProcessTime { get; set; }

        /// <summary>
        /// Ring buffer of hourly samples: generation kW, facility demand kW, dock draw kW, stored kJ.
        /// Index <see cref="HistogramIndex"/> is the next write slot (oldest when full).
        /// </summary>
        [JsonProperty]
        public List<(double GenerationKW, double DemandKW, double DockKW, double StoredKJ)> Histogram { get; set; } = new(HistogramSize);

        [JsonProperty]
        public int HistogramIndex { get; set; }

        /// <summary>Energy that may be transferred to ships without dipping below the 20% reserve.</summary>
        public double SpendableKJ =>
            Math.Max(0, EnergyStoredKJ - ReserveFraction * StorageCapacityKJ);

        public ColonyPowerDB() { }

        public ColonyPowerDB(ColonyPowerDB other)
        {
            StorageCapacityKJ = other.StorageCapacityKJ;
            EnergyStoredKJ = other.EnergyStoredKJ;
            GenerationKW = other.GenerationKW;
            DemandKW = other.DemandKW;
            PowerEfficiency = other.PowerEfficiency;
            DockChargeRateKW = other.DockChargeRateKW;
            BatteryChargeRateKW = other.BatteryChargeRateKW;
            LastProcessTime = other.LastProcessTime;
            Histogram = new List<(double, double, double, double)>(other.Histogram);
            HistogramIndex = other.HistogramIndex;
        }

        public override object Clone() => new ColonyPowerDB(this);

        public void RecordHistogramSample(double generationKW, double demandKW, double dockKW, double storedKJ)
        {
            var sample = (generationKW, demandKW, dockKW, storedKJ);
            if (Histogram.Count < HistogramSize)
            {
                Histogram.Add(sample);
                return;
            }

            Histogram[HistogramIndex] = sample;
            HistogramIndex = (HistogramIndex + 1) % HistogramSize;
        }

        /// <summary>Samples oldest → newest for UI charts.</summary>
        public IReadOnlyList<(double GenerationKW, double DemandKW, double DockKW, double StoredKJ)> GetHistogramChronological()
        {
            if (Histogram.Count == 0)
                return Array.Empty<(double, double, double, double)>();

            if (Histogram.Count < HistogramSize)
                return Histogram;

            var ordered = new List<(double, double, double, double)>(HistogramSize);
            for (int i = 0; i < HistogramSize; i++)
                ordered.Add(Histogram[(HistogramIndex + i) % HistogramSize]);
            return ordered;
        }
    }
}
