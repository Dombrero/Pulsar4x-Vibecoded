using NUnit.Framework;
using Pulsar4X.Components;
using Pulsar4X.DataStructures;
using Pulsar4X.Industry;

namespace Pulsar4X.Tests
{
    [TestFixture]
    public class ColonyInstallationClassificationTests
    {
        [Test]
        public void Ship_battery_is_component_not_colony_installation()
        {
            var design = new ComponentDesign
            {
                Name = "Battery 2t",
                ComponentMountType = ComponentMountType.ShipComponent
                    | ComponentMountType.ShipCargo
                    | ComponentMountType.Fighter,
            };

            Assert.That(IndustryTools.IsColonyInstallationDesign(design), Is.False);
        }

        [Test]
        public void Colony_battery_is_colony_installation()
        {
            var design = new ComponentDesign
            {
                Name = "Colony Battery Bank",
                ComponentMountType = ComponentMountType.ShipCargo
                    | ComponentMountType.PlanetInstallation,
            };

            Assert.That(IndustryTools.IsColonyInstallationDesign(design), Is.True);
        }

        [Test]
        public void Dual_mount_with_ship_component_is_not_colony_installation()
        {
            var design = new ComponentDesign
            {
                Name = "Legacy Dual Mount",
                ComponentMountType = ComponentMountType.ShipComponent
                    | ComponentMountType.ShipCargo
                    | ComponentMountType.PlanetInstallation,
            };

            Assert.That(IndustryTools.IsColonyInstallationDesign(design), Is.False);
        }

        [Test]
        public void Pure_facility_is_colony_installation()
        {
            var design = new ComponentDesign
            {
                Name = "Factory",
                ComponentMountType = ComponentMountType.PlanetInstallation,
            };

            Assert.That(IndustryTools.IsColonyInstallationDesign(design), Is.True);
        }

        [Test]
        public void Facility_with_cargo_transport_only_is_colony_installation()
        {
            var design = new ComponentDesign
            {
                Name = "Refinery",
                ComponentMountType = ComponentMountType.ShipCargo
                    | ComponentMountType.PlanetInstallation,
            };

            Assert.That(IndustryTools.IsColonyInstallationDesign(design), Is.True);
        }
    }
}
