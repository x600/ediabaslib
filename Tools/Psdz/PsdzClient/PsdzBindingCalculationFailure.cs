﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace PsdzClient
{
    [DataContract]
    public class PsdzBindingCalculationFailure
    {
        [DataMember]
        public string Url { get; set; }

        [DataMember]
        public int Retry { get; set; }

        [DataMember]
        public string Reason { get; set; }
    }
}