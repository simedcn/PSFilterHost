﻿/////////////////////////////////////////////////////////////////////////////////
//
// 8bf filter host for .NET
// https://github.com/0xC0000054/PSFilterHost
//
// This software is provided under the Microsoft Public License:
//   Copyright (C) 2012-2018 Nicholas Hayes
//
// See LICENSE.txt for complete licensing and attribution information.
//
/////////////////////////////////////////////////////////////////////////////////

// Adapted from:
/////////////////////////////////////////////////////////////////////////////////
// Paint.NET                                                                   //
// Copyright (C) dotPDN LLC, Rick Brewster, Tom Jackson, and contributors.     //
// Portions Copyright (C) Microsoft Corporation. All Rights Reserved.          //
// See License-pdn.txt for full licensing and attribution details.             //
//                                                                             //
/////////////////////////////////////////////////////////////////////////////////
using System;
using System.Drawing;

namespace PSFilterHostDll.BGRASurface
{
    /// <summary>
    /// Surface class for 16 bits per pixel gray scale image data.
    /// </summary>
    internal sealed class SurfaceGray16 : SurfaceBase
    {
        public SurfaceGray16(int width, int height) : this(width, height, 96.0, 96.0)
        {
        }

        public SurfaceGray16(int width, int height, double dpiX, double dpiY) : base(width, height, 2, dpiX, dpiY)
        {
        }

        public override int ChannelCount
        {
            get
            {
                return 1;
            }
        }

        public override int BitsPerChannel
        {
            get
            {
                return 16;
            }
        }

        /// <summary>
        /// Scales the data to the internal 16 bit range used by Adobe(R) Photoshop(R).
        /// </summary>
        public unsafe void ScaleToPhotoshop16BitRange()
        {
            ushort[] map = CreatePhotoshopRangeLookupTable();
            for (int y = 0; y < height; y++)
            {
                ushort* ptr = (ushort*)GetRowAddressUnchecked(y);
                ushort* ptrEnd = ptr + width;

                while (ptr < ptrEnd)
                {
                    *ptr = map[*ptr];

                    ptr++;
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
                    "Microsoft.Design",
                    "CA1031:DoNotCatchGeneralExceptionTypes",
                    Justification = "Required as Bitmap.SetResolution is documented to throw it.")]
        public override unsafe Bitmap ToGdipBitmap()
        {
            Bitmap image = null;

            const System.Drawing.Imaging.PixelFormat format = System.Drawing.Imaging.PixelFormat.Format16bppGrayScale;

            using (Bitmap temp = new Bitmap(width, height, format))
            {
                System.Drawing.Imaging.BitmapData bitmapData = temp.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, format);
                try
                {
                    byte* destScan0 = (byte*)bitmapData.Scan0;
                    int destStride = bitmapData.Stride;

                    for (int y = 0; y < height; y++)
                    {
                        ushort* src = (ushort*)GetRowAddressUnchecked(y);
                        ushort* dst = (ushort*)(destScan0 + (y * destStride));

                        for (int x = 0; x < width; x++)
                        {
                            *dst = Fix16BitRange(*src);

                            src++;
                            dst++;
                        }
                    }
                }
                finally
                {
                    temp.UnlockBits(bitmapData);
                }

                try
                {
                    temp.SetResolution((float)dpiX, (float)dpiY);
                }
                catch (Exception)
                {
                    // Ignore any errors when setting the resolution.
                }

                image = (Bitmap)temp.Clone();
            }

            return image;
        }

#if !GDIPLUS
        public override unsafe System.Windows.Media.Imaging.BitmapSource ToBitmapSource()
        {
            System.Windows.Media.PixelFormat format = System.Windows.Media.PixelFormats.Gray16;

            int destStride = ((width * format.BitsPerPixel) + 7) / 8;
            int bufferSize = destStride * height;

            IntPtr buffer = PSApi.Memory.Allocate((ulong)bufferSize, PSApi.MemoryAllocationFlags.Default);

            byte* destScan0 = (byte*)buffer;

            for (int y = 0; y < height; y++)
            {
                ushort* src = (ushort*)GetRowAddressUnchecked(y);
                ushort* dst = (ushort*)(destScan0 + (y * destStride));

                for (int x = 0; x < width; x++)
                {
                    *dst = Fix16BitRange(*src);

                    src++;
                    dst++;
                }
            }

            return System.Windows.Media.Imaging.BitmapSource.Create(width, height, dpiX, dpiY, format, null, buffer, bufferSize, destStride);
        }
#endif

        protected override unsafe void BicubicFitSurfaceUnchecked(SurfaceBase source, Rectangle dstRoi)
        {
            Rectangle roi = Rectangle.Intersect(dstRoi, Bounds);

            IntPtr rColCacheIP = BGRASurfaceMemory.Allocate(4 * (ulong)roi.Width * (ulong)sizeof(double));
            double* rColCache = (double*)rColCacheIP.ToPointer();

            int srcWidth = source.Width;
            int srcHeight = source.Height;
            long srcStride = source.Stride;

            // Precompute and then cache the value of R() for each column
            for (int dstX = roi.Left; dstX < roi.Right; ++dstX)
            {
                double srcColumn = (double)(dstX * (srcWidth - 1)) / (double)(width - 1);
                double srcColumnFloor = Math.Floor(srcColumn);
                double srcColumnFrac = srcColumn - srcColumnFloor;

                for (int m = -1; m <= 2; ++m)
                {
                    int index = (m + 1) + ((dstX - roi.Left) * 4);
                    double x = m - srcColumnFrac;
                    rColCache[index] = R(x);
                }
            }

            // Set this up so we can cache the R()'s for every row
            double* rRowCache = stackalloc double[4];

            for (int dstY = roi.Top; dstY < roi.Bottom; ++dstY)
            {
                double srcRow = (double)(dstY * (srcHeight - 1)) / (double)(height - 1);
                double srcRowFloor = Math.Floor(srcRow);
                double srcRowFrac = srcRow - srcRowFloor;
                int srcRowInt = (int)srcRow;
                ushort* dstPtr = (ushort*)GetPointAddressUnchecked(roi.Left, dstY);

                // Compute the R() values for this row
                for (int n = -1; n <= 2; ++n)
                {
                    double x = srcRowFrac - n;
                    rRowCache[n + 1] = R(x);
                }

                rColCache = (double*)rColCacheIP.ToPointer();
                ushort* srcRowPtr = (ushort*)source.GetRowAddressUnchecked(srcRowInt - 1);

                for (int dstX = roi.Left; dstX < roi.Right; dstX++)
                {
                    double srcColumn = (double)(dstX * (srcWidth - 1)) / (double)(width - 1);
                    double srcColumnFloor = Math.Floor(srcColumn);
                    int srcColumnInt = (int)srcColumn;

                    double graySum = 0;
                    double alphaSum = 0;
                    double totalWeight = 0;

                    ushort* srcPtr = (ushort*)srcRowPtr + srcColumnInt - 1;
                    for (int n = 0; n <= 3; ++n)
                    {
                        double w0 = rColCache[0] * rRowCache[n];
                        double w1 = rColCache[1] * rRowCache[n];
                        double w2 = rColCache[2] * rRowCache[n];
                        double w3 = rColCache[3] * rRowCache[n];

                        const double a0 = 255.0;
                        const double a1 = 255.0;
                        const double a2 = 255.0;
                        const double a3 = 255.0;

                        alphaSum += (a0 * w0) + (a1 * w1) + (a2 * w2) + (a3 * w3);
                        totalWeight += w0 + w1 + w2 + w3;

                        graySum += (a0 * srcPtr[0] * w0) + (a1 * srcPtr[1] * w1) + (a2 * srcPtr[2] * w2) + (a3 * srcPtr[3] * w3);

                        srcPtr = (ushort*)((byte*)srcPtr + srcStride);
                    }

                    double alpha = alphaSum / totalWeight;

                    double gray;

                    if (alpha == 0)
                    {
                        gray = 0;
                    }
                    else
                    {
                        gray = graySum / alphaSum;
                        // add 0.5 to ensure truncation to uint results in rounding
                        alpha += 0.5;
                        gray += 0.5;
                    }

                    *dstPtr = (ushort)gray;
                    ++dstPtr;
                    rColCache += 4;
                } // for (dstX...
            } // for (dstY...

            BGRASurfaceMemory.Free(rColCacheIP);
        }

        protected override unsafe void BicubicFitSurfaceChecked(SurfaceBase source, Rectangle dstRoi)
        {
            Rectangle roi = Rectangle.Intersect(dstRoi, Bounds);

            IntPtr rColCacheIP = BGRASurfaceMemory.Allocate(4 * (ulong)roi.Width * (ulong)sizeof(double));
            double* rColCache = (double*)rColCacheIP.ToPointer();

            int srcWidth = source.Width;
            int srcHeight = source.Height;
            long srcStride = source.Stride;

            // Precompute and then cache the value of R() for each column
            for (int dstX = roi.Left; dstX < roi.Right; ++dstX)
            {
                double srcColumn = (double)(dstX * (srcWidth - 1)) / (double)(width - 1);
                double srcColumnFloor = Math.Floor(srcColumn);
                double srcColumnFrac = srcColumn - srcColumnFloor;

                for (int m = -1; m <= 2; ++m)
                {
                    int index = (m + 1) + ((dstX - roi.Left) * 4);
                    double x = m - srcColumnFrac;
                    rColCache[index] = R(x);
                }
            }

            // Set this up so we can cache the R()'s for every row
            double* rRowCache = stackalloc double[4];

            for (int dstY = roi.Top; dstY < roi.Bottom; ++dstY)
            {
                double srcRow = (double)(dstY * (srcHeight - 1)) / (double)(height - 1);
                double srcRowFloor = (double)Math.Floor(srcRow);
                double srcRowFrac = srcRow - srcRowFloor;
                int srcRowInt = (int)srcRow;
                ushort* dstPtr = (ushort*)GetPointAddressUnchecked(roi.Left, dstY);

                // Compute the R() values for this row
                for (int n = -1; n <= 2; ++n)
                {
                    double x = srcRowFrac - n;
                    rRowCache[n + 1] = R(x);
                }

                // See Perf Note below
                //int nFirst = Math.Max(-srcRowInt, -1);
                //int nLast = Math.Min(source.height - srcRowInt - 1, 2);

                for (int dstX = roi.Left; dstX < roi.Right; dstX++)
                {
                    double srcColumn = (double)(dstX * (srcWidth - 1)) / (double)(width - 1);
                    double srcColumnFloor = Math.Floor(srcColumn);
                    int srcColumnInt = (int)srcColumn;

                    double graySum = 0;
                    double alphaSum = 0;
                    double totalWeight = 0;

                    // See Perf Note below
                    //int mFirst = Math.Max(-srcColumnInt, -1);
                    //int mLast = Math.Min(source.width - srcColumnInt - 1, 2);

                    ushort* srcPtr = (ushort*)source.GetPointAddressUnchecked(srcColumnInt - 1, srcRowInt - 1);

                    for (int n = -1; n <= 2; ++n)
                    {
                        int srcY = srcRowInt + n;

                        for (int m = -1; m <= 2; ++m)
                        {
                            // Perf Note: It actually benchmarks faster on my system to do
                            // a bounds check for every (m,n) than it is to limit the loop
                            // to nFirst-Last and mFirst-mLast.
                            // I'm leaving the code above, albeit commented out, so that
                            // benchmarking between these two can still be performed.
                            if (source.IsVisible(srcColumnInt + m, srcY))
                            {
                                double w0 = rColCache[(m + 1) + (4 * (dstX - roi.Left))];
                                double w1 = rRowCache[n + 1];
                                double w = w0 * w1;

                                graySum += *srcPtr * w * 255.0;
                                alphaSum += 255.0 * w;

                                totalWeight += w;
                            }

                            ++srcPtr;
                        }

                        srcPtr = (ushort*)((byte*)(srcPtr - 4) + srcStride);
                    }

                    double alpha = alphaSum / totalWeight;
                    double gray;

                    if (alpha == 0)
                    {
                        gray = 0;
                    }
                    else
                    {
                        gray = graySum / alphaSum;

                        // add 0.5 to ensure truncation to ushort results in rounding
                        gray += 0.5;
                    }

                    *dstPtr = (ushort)gray;
                    ++dstPtr;
                } // for (dstX...
            } // for (dstY...

            BGRASurfaceMemory.Free(rColCacheIP);
        }

        public override unsafe void SuperSampleFitSurface(SurfaceBase source)
        {
            Rectangle dstRoi2 = Rectangle.Intersect(source.Bounds, Bounds);

            int srcHeight = source.Height;
            int srcWidth = source.Width;
            long srcStride = source.Stride / 2;

            for (int dstY = dstRoi2.Top; dstY < dstRoi2.Bottom; ++dstY)
            {
                double srcTop = (double)(dstY * srcHeight) / (double)height;
                double srcTopFloor = Math.Floor(srcTop);
                double srcTopWeight = 1 - (srcTop - srcTopFloor);
                int srcTopInt = (int)srcTopFloor;

                double srcBottom = (double)((dstY + 1) * srcHeight) / (double)height;
                double srcBottomFloor = Math.Floor(srcBottom - 0.00001);
                double srcBottomWeight = srcBottom - srcBottomFloor;
                int srcBottomInt = (int)srcBottomFloor;

                ushort* dstPtr = (ushort*)GetPointAddressUnchecked(dstRoi2.Left, dstY);

                for (int dstX = dstRoi2.Left; dstX < dstRoi2.Right; ++dstX)
                {
                    double srcLeft = (double)(dstX * srcWidth) / (double)width;
                    double srcLeftFloor = Math.Floor(srcLeft);
                    double srcLeftWeight = 1 - (srcLeft - srcLeftFloor);
                    int srcLeftInt = (int)srcLeftFloor;

                    double srcRight = (double)((dstX + 1) * srcWidth) / (double)width;
                    double srcRightFloor = Math.Floor(srcRight - 0.00001);
                    double srcRightWeight = srcRight - srcRightFloor;
                    int srcRightInt = (int)srcRightFloor;

                    double graySum = 0;
                    double alphaSum = 0;

                    // left fractional edge
                    ushort* srcLeftPtr = (ushort*)source.GetPointAddressUnchecked(srcLeftInt, srcTopInt + 1);

                    for (int srcY = srcTopInt + 1; srcY < srcBottomInt; ++srcY)
                    {
                        const double a = 255.0;
                        graySum += srcLeftPtr[0] * srcLeftWeight * a;
                        alphaSum += a * srcLeftWeight;
                        srcLeftPtr = (ushort*)(srcLeftPtr + srcStride);
                    }

                    // right fractional edge
                    ushort* srcRightPtr = (ushort*)source.GetPointAddressUnchecked(srcRightInt, srcTopInt + 1);
                    for (int srcY = srcTopInt + 1; srcY < srcBottomInt; ++srcY)
                    {
                        const double a = 255.0;
                        graySum += srcRightPtr[0] * srcRightWeight * a;
                        alphaSum += a * srcRightWeight;
                        srcRightPtr = (ushort*)(srcRightPtr + srcStride);
                    }

                    // top fractional edge
                    ushort* srcTopPtr = (ushort*)source.GetPointAddressUnchecked(srcLeftInt + 1, srcTopInt);
                    for (int srcX = srcLeftInt + 1; srcX < srcRightInt; ++srcX)
                    {
                        const double a = 255.0;
                        graySum += srcTopPtr[0] * srcTopWeight * a;
                        alphaSum += a * srcTopWeight;
                        ++srcTopPtr;
                    }

                    // bottom fractional edge
                    ushort* srcBottomPtr = (ushort*)source.GetPointAddressUnchecked(srcLeftInt + 1, srcBottomInt);
                    for (int srcX = srcLeftInt + 1; srcX < srcRightInt; ++srcX)
                    {
                        const double a = 255.0;
                        graySum += srcBottomPtr[0] * srcBottomWeight * a;
                        alphaSum += 255.0 * srcBottomWeight;
                        ++srcBottomPtr;
                    }

                    // center area
                    for (int srcY = srcTopInt + 1; srcY < srcBottomInt; ++srcY)
                    {
                        ushort* srcPtr = (ushort*)source.GetPointAddressUnchecked(srcLeftInt + 1, srcY);

                        for (int srcX = srcLeftInt + 1; srcX < srcRightInt; ++srcX)
                        {
                            graySum += (double)srcPtr[0] * 255.0;
                            alphaSum += 255.0;
                            ++srcPtr;
                        }
                    }

                    // four corner pixels
                    ushort srcTL = *(ushort*)source.GetPointAddress(srcLeftInt, srcTopInt);
                    const double srcTLA = 255.0;
                    graySum += srcTL * (srcTopWeight * srcLeftWeight) * srcTLA;
                    alphaSum += srcTLA * (srcTopWeight * srcLeftWeight);

                    ushort srcTR = *(ushort*)source.GetPointAddress(srcRightInt, srcTopInt);
                    const double srcTRA = 255.0;
                    graySum += srcTR * (srcTopWeight * srcRightWeight) * srcTRA;
                    alphaSum += srcTRA * (srcTopWeight * srcRightWeight);

                    ushort srcBL = *(ushort*)source.GetPointAddress(srcLeftInt, srcBottomInt);
                    const double srcBLA = 255.0;
                    graySum += srcBL * (srcBottomWeight * srcLeftWeight) * srcBLA;
                    alphaSum += srcBLA * (srcBottomWeight * srcLeftWeight);

                    ushort srcBR = *(ushort*)source.GetPointAddress(srcRightInt, srcBottomInt);
                    const double srcBRA = 255.0;
                    graySum += srcBR * (srcBottomWeight * srcRightWeight) * srcBRA;
                    alphaSum += srcBRA * (srcBottomWeight * srcRightWeight);

                    double area = (srcRight - srcLeft) * (srcBottom - srcTop);

                    double alpha = alphaSum / area;
                    double gray;

                    if (alpha == 0)
                    {
                        gray = 0;
                    }
                    else
                    {
                        gray = graySum / alphaSum;
                    }

                    // add 0.5 so that rounding goes in the direction we want it to
                    gray += 0.5;

                    dstPtr[0] = (ushort)gray;
                    ++dstPtr;
                }
            }
        }
    }
}
