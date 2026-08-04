using System;
using System.Collections.Generic;
using Pulsar4X.Orbital;
using Pulsar4X.Engine;
using Pulsar4X.Datablobs;

namespace Pulsar4X.Movement
{

    public struct Manuver
    {
        public enum ManuverType
        {
            Drift,
            NewtonSimple,
            NewtonThrust,
            Warp,
            Wormhole
        }
        public ManuverType TypeOfManuver;
        public DateTime StartDateTime;
        public DateTime EndDateTime;
        public KeplerElements StartKepler;
        public KeplerElements EndKepler;
        public Entity StartSOIParent = Entity.InvalidEntity;
        public Entity EndSOIParent = Entity.InvalidEntity;
        public Manuver()
        {
            StartSOIParent = Entity.InvalidEntity;
            EndSOIParent = Entity.InvalidEntity;
        }
    }
    public class NavSequenceDB : BaseDataBlob
    {
        public string? CurrentActivity { get; internal set; }
        public List<Manuver> ManuverNodes = new List<Manuver>();


        public NavSequenceDB() { }

        public NavSequenceDB(NavSequenceDB db)
        {
            CurrentActivity = db.CurrentActivity;
            ManuverNodes = new List<Manuver>(db.ManuverNodes);
        }

        internal void AddManuver(Manuver.ManuverType type, DateTime startDate, Entity StartParent, KeplerElements startKE, DateTime endDate, Entity EndParent, KeplerElements endKE)
        {
            var node = new Manuver()
            {
                TypeOfManuver = type,
                StartDateTime = startDate,
                StartSOIParent = StartParent,
                StartKepler = startKE,
                EndDateTime = endDate,
                EndSOIParent = EndParent,
                EndKepler = endKE,
            };
            ManuverNodes.Add(node);
            StartParent.AttachedManager.ManagerSubpulses.AddEntityInterupt(startDate, nameof(NavSequenceProcessor), OwningEntity);
            EndParent.AttachedManager.ManagerSubpulses.AddEntityInterupt(endDate, nameof(NavSequenceProcessor), OwningEntity);
        }

        internal void AddManuver(Manuver manuver)
        {
            ManuverNodes.Add(manuver);
            var startDate = manuver.StartDateTime;
            var endDate = manuver.EndDateTime;
            var startParentSubpulse = manuver.StartSOIParent.AttachedManager.ManagerSubpulses;
            var endParentSubpulse = manuver.EndSOIParent.AttachedManager.ManagerSubpulses;
            startParentSubpulse.AddEntityInterupt(startDate, nameof(NavSequenceProcessor), OwningEntity);
            endParentSubpulse.AddEntityInterupt(endDate, nameof(NavSequenceProcessor), OwningEntity);
        }

        public override object Clone()
        {
            return new NavSequenceDB(this);
        }
    }
}
