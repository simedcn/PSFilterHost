﻿/////////////////////////////////////////////////////////////////////////////////
//
// 8bf filter host for .NET
// http://psfilterhost.codeplex.com/
//
// This software is provided under the Microsoft Public License:
//   Copyright (C) 2012-2015 Nicholas Hayes
// 
// See LICENSE.txt for complete licensing and attribution information.
//
/////////////////////////////////////////////////////////////////////////////////

using System;

namespace PSFilterHostDll.PSApi
{
    [Serializable]
    internal sealed class AETEParameter
    {
        public string name;
        public uint key;
        public uint type;
        public string desc;
        public short flags;
    }

    [Serializable]
    internal sealed class AETEEnums
    {
        public uint type;
        public short count;
        public AETEEnum[] enums;
    }

    [Serializable]
    internal sealed class AETEEnum
    {
        public string name;
        public uint type;
        public string desc;
    }

    [Serializable]
    internal sealed class AETEEvent
    {
        public string vendor;
        public string desc;
        public int eventClass;
        public int type;
        public uint replyType;
        public uint paramType;
        public short flags;
        public AETEParameter[] parameters;
        public AETEEnums[] enums;
    }

    [Serializable]
    internal sealed class PluginAETE
    {
        public int major;
        public int minor;
        public short suiteLevel;
        public short suiteVersion;
        public AETEEvent scriptEvent;

        public PluginAETE(int major, int minor, short suiteLevel, short suiteVersion, AETEEvent scriptEvent)
        {
            this.major = major;
            this.minor = minor;
            this.suiteLevel = suiteLevel;
            this.suiteVersion = suiteVersion;
            this.scriptEvent = scriptEvent;
        }
    } 
}
