﻿using System.Xml;

using GTEventGenerator.Entities;

namespace GTEventGenerator
{
    public class RaceEntry
    {
        public bool IsAI { get; set; } = true;

        public string DriverName { get; set; } = "Unnamed";
        public string DriverRegion { get; set; } = "PDI";

        private int _baseSkill = 80;
        public int BaseSkill
        {
            get => _baseSkill; 
            set
            {
                if (value <= 200 && value >= 0)
                    _baseSkill = value;
            }
        }

        private int _brakingSkill = 80;
        public int BrakingSkill
        {
            get => _brakingSkill;
            set
            {
                if (value <= 200 && value >= 0)
                    _brakingSkill = value;
            }
        }

        private int _corneringSKill = 80;
        public int CorneringSkill
        {
            get => _corneringSKill;
            set
            {
                if (value <= 200 && value >= 0)
                    _corneringSKill = value;
            }
        }

        private int _accelSkill = 80;
        public int AccelSkill
        {
            get => _accelSkill;
            set
            {
                if (value <= 200 && value >= 0)
                    _accelSkill = value;
            }
        }
        
        private int _delay
        {
            get => _delay;
            set
            {
                if (value <= 3_600_000 && value >= -1)
                    _delay = value;
            }
        }

        public int Delay { get; set; }
        public int raceBucket { get; set; }

        private int _initialVelocity = -1;
        public int InitialVelocity
        {
            get => _initialVelocity;
            set
            {
                if (value <= 200 && value >= -1)
                    _initialVelocity = value;
            }
        }

        private float _initialVCoord;
        public float InitialVCoord
        {
            get => _initialVCoord;
            set
            {
                if (value <= 200)
                    _initialVCoord = value;
            }
        }

        public string CarLabel { get; set; }
        public string ActualCarName { get; set; }

        public int ColorIndex { get; set; }
        public TireType TireFront { get; set; } = TireType.NONE_SPECIFIED;
        public TireType TireRear { get; set; } = TireType.NONE_SPECIFIED;

        public RaceEntry()
        {
            this.Delay = 0;
            this.raceBucket = 0;
            this.InitialVelocity = 0;
            this.InitialVCoord = 0;
        }

        public void WriteToXml(XmlWriter xml)
        {
            if (IsAI)
                xml.WriteStartElement("entry_base");
            else
                xml.WriteStartElement("entry");

            xml.WriteElementFloatIfSet("initial_position", InitialVCoord);
            xml.WriteElementIntIfSet("initial_velocity", InitialVelocity);

            if (IsAI)
            {
                xml.WriteElementValue("driver_name", DriverName);
                xml.WriteElementValue("driver_region", DriverRegion);
            }

            xml.WriteElementInt("delay", Delay);
            xml.WriteStartElement("car");
            xml.WriteAttributeString("color", ColorIndex.ToString());
            xml.WriteAttributeString("label", CarLabel);
            xml.WriteEndElement();

            if (IsAI)
            {
                xml.WriteElementInt("ai_skill", BaseSkill);
                xml.WriteElementInt("ai_skill_accelerating", AccelSkill);
                xml.WriteElementInt("ai_skill_breaking", BrakingSkill);
                xml.WriteElementInt("ai_skill_cornering", CorneringSkill);
                xml.WriteElementInt("ai_roughness", -1);
            }

            if (TireFront != TireType.NONE_SPECIFIED)
                xml.WriteElementValue("tire_f", TireFront.ToString());
            if (TireRear != TireType.NONE_SPECIFIED)
                xml.WriteElementValue("tire_r", TireRear.ToString());

            xml.WriteEndElement();
        }
    }
}
