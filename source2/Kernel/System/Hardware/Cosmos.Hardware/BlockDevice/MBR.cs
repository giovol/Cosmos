﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cosmos.Hardware.BlockDevice {
  // Its not a BlockDevice, but its related to "fixed" devices
  // and necessary to create partition block devices
  // Im not comfortable with MBR and Partition being in Hardware ring and would prefer
  // them in the system ring, but there are issues relating to moving it there.
  public class MBR {
    // TODO Lock this so other code cannot add/remove/modify the list
    // Can make a locked list class which wraps a list<>
    public List<PartInfo> Partitions = new List<PartInfo>();

    public class PartInfo {
      public readonly byte SystemID;
      public readonly UInt32 StartSector;
      public readonly UInt32 SectorCount;

      public PartInfo(byte aSystemID, UInt32 aStartSector, UInt32 aSectorCount) {
        SystemID = aSystemID;
        StartSector = aStartSector;
        SectorCount = aSectorCount;
      }
    }

    public MBR(byte[] aMBR) {
      ParsePartition(aMBR, 446);
      ParsePartition(aMBR, 462);
      ParsePartition(aMBR, 478);
      ParsePartition(aMBR, 494);
    }

    protected void ParsePartition(byte[] aMBR, int aLoc) {
      byte xSystemID = aMBR[aLoc + 4];
      // SystemID = 0 means no partition
      if (xSystemID != 0) {
        // TODO - Make Bitconvertor work and change all these
        // TODO - Make a note about BitConvertor - change to high speed ASM plugs / cosmos calls
        UInt32 xStartSector = (UInt32)(aMBR[aLoc + 11] << 24 | aMBR[aLoc + 10] << 16 | aMBR[aLoc + 9] << 8 | aMBR[aLoc + 8]);
        UInt32 xSectorCount = (UInt32)(aMBR[aLoc + 15] << 24 | aMBR[aLoc + 14] << 16 | aMBR[aLoc + 13] << 8 | aMBR[aLoc + 12]);

        var xPartInfo = new PartInfo(xSystemID, xStartSector, xSectorCount);
        Partitions.Add(xPartInfo);
      }
    }

  }
}
