﻿/////////////////////////////////////////////////////////////////////////////////
//
// Adobe(R) Photoshop(R) filter host for .NET
// http://psfilterhost.codeplex.com/
//
// This software is provided under the Microsoft Public License:
//   Copyright (C) 2012-2013 Nicholas Hayes
// 
// See LICENSE.txt for complete licensing and attribution information.
//
/////////////////////////////////////////////////////////////////////////////////

using System;

namespace HostTest
{
    internal sealed class CanvasZoomChangingEventArgs : EventArgs
    {
        private readonly float newScale;
        private readonly float minScale;
        private readonly float maxScale;

        public float NewZoom
        {
            get
            {
                return newScale;
            }
        }

        public float MinZoom
        {
            get
            {
                return minScale;
            }
        }

        public float MaxZoom
        {
            get
            {
                return maxScale;
            }
        }

        public CanvasZoomChangingEventArgs(float scale, float min, float max)
        {
            this.newScale = scale;
            this.minScale = min;
            this.maxScale = max;
        }
    }
}
