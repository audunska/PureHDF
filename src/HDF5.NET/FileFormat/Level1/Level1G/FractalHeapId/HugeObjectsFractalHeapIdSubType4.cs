﻿using System.IO;

namespace HDF5.NET
{
    public class HugeObjectsFractalHeapIdSubType4 : FractalHeapId
    {
        #region Constructors

        public HugeObjectsFractalHeapIdSubType4(BinaryReader reader, Superblock superblock) : base(reader)
        {
            // address
            this.Address = superblock.ReadOffset();

            // length
            this.Length = superblock.ReadLength();

            // filter mask
            this.FilterMask = reader.ReadUInt32();

            // de-filtered size
            this.DeFilteredSize = superblock.ReadLength();
        }

        #endregion

        #region Properties

        public ulong Address { get; set; }
        public ulong Length { get; set; }
        public uint FilterMask { get; set; }
        public ulong DeFilteredSize { get; set; }

        protected override FractalHeapIdType ExpectedType => FractalHeapIdType.Huge;

        #endregion
    }
}
