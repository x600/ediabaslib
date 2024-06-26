﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using BMW.Rheingold.Psdz.Model.Ecu;
using BMW.Rheingold.Psdz.Model.Localization;

namespace BMW.Rheingold.Psdz.Model.Events
{
    [KnownType(typeof(PsdzEcuIdentifier))]
    [DataContract]
    public class PsdzEvent : ILocalizableMessage, IPsdzEvent
    {
        [DataMember]
        public IPsdzEcuIdentifier EcuId { get; set; }

        [DataMember]
        public string EventId { get; set; }

        [DataMember]
        public string Message { get; set; }

        [DataMember]
        public int MessageId { get; set; }

        [DataMember]
        public long Timestamp { get; set; }
    }
}
