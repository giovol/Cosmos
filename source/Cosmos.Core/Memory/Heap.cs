﻿using System;
using Cosmos.Debug.Kernel;
using IL2CPU.API;

namespace Cosmos.Core.Memory
{

    /// <summary>
    /// Flags to track an object status
    /// All higher values in the ushort are used to track count of static counts
    /// </summary>
    public enum ObjectGCStatus : ushort
    {
        None = 0,
        Hit = 1
    }
    /// <summary>
    /// Heap class.
    /// </summary>
    public static unsafe class Heap
    {
        private static uint* StackStart;
        /// <summary>
        /// Init heap.
        /// </summary>
        /// <exception cref="Exception">Thrown on fatal error, contact support.</exception>
        public static unsafe void Init()
        {
            StackStart = (uint*)CPU.GetStackStart();
            HeapSmall.Init();
            HeapMedium.Init();
            HeapLarge.Init();
        }

        /// <summary>
        /// Alloc memory block, of a given size.
        /// </summary>
        /// <param name="aSize">A size of block to alloc, in bytes.</param>
        /// <returns>Byte pointer to the start of the block.</returns>
        public static byte* Alloc(uint aSize)
        {
            CPU.DisableInterrupts();

            if (aSize <= HeapSmall.mMaxItemSize)
            {
                byte* ptr = HeapSmall.Alloc((ushort)aSize);
                CPU.EnableInterrupts();
                return ptr;
            }
            else if (aSize <= HeapMedium.MaxItemSize)
            {
                byte* ptr = HeapMedium.Alloc(aSize);
                CPU.EnableInterrupts();
                return ptr;
            }
            else
            {
                byte* ptr = HeapLarge.Alloc(aSize);
                CPU.EnableInterrupts();
                return ptr;
            }
        }

        /// <summary>
        /// Allocates memory and returns the pointer as uint
        /// </summary>
        /// <param name="aSize">Size of memory to allocate</param>
        /// <returns></returns>
        public static uint SafeAlloc(uint aSize)
        {
            return (uint)Alloc(aSize);
        }

        // Keep as void* and not byte* or other. Reduces typecasting from callers
        // who may have typed the pointer to their own needs.
        /// <summary>
        /// Free a heap item.
        /// </summary>
        /// <param name="aPtr">A pointer to the heap item to be freed.</param>
        /// <exception cref="Exception">Thrown if: 
        /// <list type="bullet">
        /// <item>Page type is not found.</item>
        /// <item>Heap item not found in RAT.</item>
        /// </list>
        /// </exception>
        public static void Free(void* aPtr)
        {
            CPU.DisableInterrupts();

            //TODO find a better way to remove the double look up here for GetPageType and then again in the
            // .Free methods which actually free the entries in the RAT.
            //Debugger.DoSendNumber(0x77);
            //Debugger.DoSendNumber((uint)aPtr);
            var xType = RAT.GetPageType(aPtr);
            switch (xType)
            {
                case RAT.PageType.HeapSmall:
                    HeapSmall.Free(aPtr);
                    break;
                case RAT.PageType.HeapMedium:
                case RAT.PageType.HeapLarge:
                    HeapLarge.Free(aPtr);
                    break;

                default:
                    throw new Exception("Heap item not found in RAT.");
            }

            CPU.EnableInterrupts();
        }

        /// <summary>
        /// Collects all unreferenced objects after identifying them first
        /// </summary>
        /// <returns>Number of objects freed</returns>
        public static int Collect()
        {
            // Mark and sweep objects from roots
            // 1. Check if a page is in use if medium/large mark and sweep object
            // 2. Go throught the SMT table for small objects and go through pages by size
            //    mark and sweep all allocated objects as well

            // Medium and large objects
            for (int ratIndex = 0; ratIndex < RAT.TotalPageCount; ratIndex++)
            {
                var pageType = *(RAT.mRAT + ratIndex);
                if (pageType == RAT.PageType.HeapMedium || pageType == RAT.PageType.HeapLarge)
                {
                    var pagePtr = RAT.RamStart + ratIndex * RAT.PageSize;
                    if (*(ushort*)(pagePtr + 3) != 0)
                    {
                        MarkAndSweepObject(pagePtr + HeapLarge.PrefixBytes);
                    }
                }
            }

            // Small objects
            // we go one size at a time
            var rootSMTPtr = HeapSmall.SMT->First;
            while (rootSMTPtr != null)
            {
                uint size = rootSMTPtr->Size;
                var objectSize = size + HeapSmall.PrefixItemBytes;
                uint objectsPerPage = RAT.PageSize / objectSize;

                var smtBlock = rootSMTPtr->First;

                while (smtBlock != null)
                {
                    var pagePtr = smtBlock->PagePtr;
                    for (int i = 0; i < objectsPerPage; i++)
                    {

                        if (*(ushort*)(pagePtr + i * objectSize + 1) > 1) // 0 means not found and 1 means marked
                        {
                            MarkAndSweepObject(pagePtr + i * objectSize + HeapSmall.PrefixItemBytes);
                        }
                    }

                    smtBlock = smtBlock->NextBlock;
                }

                rootSMTPtr = rootSMTPtr->LargerSize;
            }

            // Mark and sweep objects from stack
            uint* currentStackPointer = (uint*)CPU.GetEBPValue();
            while (StackStart != currentStackPointer)
            {
                if (RAT.RamStart < (byte*)*currentStackPointer && (byte*)*currentStackPointer < RAT.HeapEnd)
                {
                    if ((RAT.GetPageType((uint*)*currentStackPointer) & RAT.PageType.GCManaged) == RAT.PageType.GCManaged)
                    {
                        MarkAndSweepObject((uint*)*currentStackPointer);
                    }
                }
                currentStackPointer += 1;
            }

            // Free all unreferenced and reset hit flag
            // This means we do the same transversal as we did before of the heap
            // but we done have to touch the stack again
            int freed = 0;
            // Medium and large objects
            for (int ratIndex = 0; ratIndex < RAT.TotalPageCount; ratIndex++)
            {
                var pageType = *(RAT.mRAT + ratIndex);
                if (pageType == RAT.PageType.HeapMedium || pageType == RAT.PageType.HeapLarge)
                {
                    var pagePointer = RAT.RamStart + ratIndex * RAT.PageSize;
                    if (*((ushort*)(pagePointer + HeapLarge.PrefixBytes) - 1) == 0)
                    {
                        Free(pagePointer + HeapLarge.PrefixBytes);
                        freed += 1;
                    }
                    else
                    {
                        *((ushort*)(pagePointer + HeapLarge.PrefixBytes) - 1) &= (ushort)~ObjectGCStatus.Hit;
                    }
                }
            }

            // Small objects
            // we go one size at a time
            rootSMTPtr = HeapSmall.SMT->First;
            while (rootSMTPtr != null)
            {
                uint size = rootSMTPtr->Size;
                uint objectSize = size + HeapSmall.PrefixItemBytes;
                uint objectsPerPage = RAT.PageSize / objectSize;

                SMTBlock* smtBlock = rootSMTPtr->First;

                while (smtBlock != null)
                {
                    byte* pagePtr = smtBlock->PagePtr;
                    for (int i = 0; i < objectsPerPage; i++)
                    {
                        if (*(ushort*)(pagePtr + i * objectSize) != 0)
                        {
                            if (*((ushort*)(pagePtr + i * objectSize) + 1) == 0)
                            {
                                Free(pagePtr + i * objectSize + HeapSmall.PrefixItemBytes);
                                freed += 1;
                            }
                            else
                            {
                                *((ushort*)(pagePtr + i * objectSize) + 1) &= (ushort)~ObjectGCStatus.Hit;
                            }
                        }
                    }
                    smtBlock = smtBlock->NextBlock;
                }

                rootSMTPtr = rootSMTPtr->LargerSize;
            }

            return freed;
        }

        /// <summary>
        /// Marks a GC managed object as referenced and recursivly marks child objects as well
        /// </summary>
        /// <param name="aPtr"></param>
        public static void MarkAndSweepObject(void* aPtr)
        {
            var gcPointer = (ObjectGCStatus*)aPtr;

            if ((gcPointer[-1] & ObjectGCStatus.Hit) == ObjectGCStatus.Hit)
            {
                return; // we already hit this object
            }

            // Mark
            gcPointer[-1] |= ObjectGCStatus.Hit;

            // Sweep

            uint* obj = (uint*)aPtr;
            // Check what we are dealing with
            if (*(obj + 1) == (uint)ObjectUtils.InstanceTypeEnum.NormalObject)
            {
                if (_StringType == 0)
                {
                    _StringType = GetStringTypeID();
                }
                var type = *obj;
                // Deal with strings first
                if (type == _StringType)
                {
                    return; // we are done since they dont hold any reference to fields
                }

                SweepTypedObject(obj, type);
            }
            else if (*(obj + 1) == (uint)ObjectUtils.InstanceTypeEnum.Array)
            {
                var elementType = *obj;
                var length = *(obj + 2);
                var size = *(obj + 3);
                if (VTablesImpl.IsValueType(elementType))
                {
                    if (VTablesImpl.IsStruct(elementType))
                    {
                        for (int i = 0; i < length; i++)
                        {
                            var location = (uint*)((byte*)obj + size* i) + 4;
                            SweepTypedObject(location, elementType);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < length; i++)
                    {
                        var location = (uint*)((byte*)obj + size * i) + 4 + 1;
                        if (*location != 0)
                        {
                            location = *(uint**)location;
                            if (RAT.GetPageType(location) == RAT.PageType.HeapSmall) // so we dont try free string literals
                            {
                                MarkAndSweepObject(location);
                            }
                        }
                    }
                }
            }
            else if (*(obj + 1) == (uint)ObjectUtils.InstanceTypeEnum.BoxedValueType)
            {
                // do nothing
            }
        }

        /// <summary>
        /// Marks all objects referenced
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="type"></param>
        public static void SweepTypedObject(uint* obj, uint type)
        {
            if (obj == null)
            {
                return;
            }
            uint fields = VTablesImpl.GetGCFieldCount(type);
            var offsets = VTablesImpl.GetGCFieldOffsets(type);
            var types = VTablesImpl.GetGCFieldTypes(type);
            for (int i = 0; i < fields; i++)
            {
                if (!VTablesImpl.IsValueType(types[i]))
                {
                    var location = (uint*)((byte*)obj + offsets[i]) + 1; // +1 since we are only using 32bits from the 64bit
                    if (*location != 0) // Check if its null
                    {
                        location = *(uint**)location;
                        if (RAT.GetPageType(location) == RAT.PageType.HeapSmall)
                        {
                            MarkAndSweepObject(location);
                        }
                    }
                }
                else if (VTablesImpl.IsStruct(types[i]))
                {
                    var obj1 = (uint*)((byte*)obj + offsets[i]);
                    SweepTypedObject(obj1, types[i]);
                }
            }
        }

        /// <summary>
        /// Stores the ID used for strings for quick comparison in CleanUp
        /// </summary>
        private static uint _StringType = 0;

        /// <summary>
        /// This is plugged using asm and gets the value for _StringType 
        /// </summary>
        /// <returns></returns>
        private static uint GetStringTypeID()
        {
            return UInt32.MaxValue; // so that tests still pass return bogus value
        }
    }
}
