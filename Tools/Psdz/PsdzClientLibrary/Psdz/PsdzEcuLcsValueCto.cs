﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using BMW.Rheingold.Psdz.Model.Ecu;

namespace BMW.Rheingold.Psdz.Model.Sfa
{
    [DataContract]
    [KnownType(typeof(PsdzEcuIdentifier))]
    public class PsdzEcuLcsValueCto : IPsdzEcuLcsValueCto
    {
        [DataMember]
        public IPsdzEcuIdentifier EcuIdentifier { get; set; }

        [DataMember]
        public int LcsNumber { get; set; }

        [DataMember]
        public int LcsValue { get; set; }
    }
}
