﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;

namespace PsdzClient
{
    abstract class PsdzDuplexClientBase<TChannel, TCallback> : PsdzClientBase<TChannel> where TChannel : class where TCallback : class
    {
        protected PsdzDuplexClientBase(TCallback callbackInstance, Binding binding, EndpointAddress remoteAddress) : base(new DuplexChannelFactory<TChannel>(callbackInstance, binding, remoteAddress))
        {
        }
    }
}
