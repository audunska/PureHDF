﻿using System.Runtime.InteropServices;

namespace HDF5.NET.Tests
{
    public struct TestStructStringL1
    {
        public float FloatValue;

        [MarshalAs(UnmanagedType.LPStr)]
        public string StringValue1;

        [MarshalAs(UnmanagedType.LPStr)]
        public string StringValue2;

        public TestStructStringL2 L2Struct;
    }
}
