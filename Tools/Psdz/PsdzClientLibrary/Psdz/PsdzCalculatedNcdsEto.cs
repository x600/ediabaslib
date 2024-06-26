﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace BMW.Rheingold.Psdz.Model.SecureCoding
{
    [DataContract]
    [KnownType(typeof(PsdzSgbmId))]
    [KnownType(typeof(PsdzNcd))]
    public class PsdzCalculatedNcdsEto : IPsdzCalculatedNcdsEto
    {
        [DataMember]
        public string Btld { get; set; }

        [DataMember]
        public IPsdzSgbmId CafdId { get; set; }

        [DataMember]
        public IPsdzNcd Ncd { get; set; }
    }
}
