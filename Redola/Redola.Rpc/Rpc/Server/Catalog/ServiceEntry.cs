﻿using System;

namespace Redola.Rpc
{
    public class ServiceEntry
    {
        public ServiceEntry()
        {
        }

        public ServiceEntry(Type declaringType, object serviceInstance)
        {
            this.DeclaringType = declaringType;
            this.ServiceInstance = serviceInstance;
        }

        public Type DeclaringType { get; set; }
        public object ServiceInstance { get; set; }

        public override string ToString()
        {
            return string.Format("Type[{0}]", DeclaringType);
        }
    }
}
