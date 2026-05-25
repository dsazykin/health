using System;
using System.Collections.Generic;

namespace HealthDashboard.Core.Analytics
{
    public static class LttbDownsampler
    {
        public class DataPoint
        {
            public double X { get; set; }
            public double Y { get; set; }
        }

        public static List<DataPoint> Downsample(List<DataPoint> data, int threshold)
        {
            int dataLength = data.Count;
            if (threshold >= dataLength || threshold <= 2)
                return data; // Nothing to downsample

            var sampled = new List<DataPoint>(threshold);
            
            // Always include the first point
            sampled.Add(data[0]);

            // Bucket size. Leave room for start and end points
            double bucketSize = (double)(dataLength - 2) / (threshold - 2);

            int a = 0;
            int nextA = 0;

            for (int i = 0; i < threshold - 2; i++)
            {
                // Calculate bucket range
                int bucketStart = (int)Math.Floor((i + 0) * bucketSize) + 1;
                int bucketEnd = (int)Math.Floor((i + 1) * bucketSize) + 1;
                bucketEnd = Math.Min(bucketEnd, dataLength);

                // Calculate center of gravity for the next bucket (c)
                int nextBucketStart = (int)Math.Floor((i + 1) * bucketSize) + 1;
                int nextBucketEnd = (int)Math.Floor((i + 2) * bucketSize) + 1;
                nextBucketEnd = Math.Min(nextBucketEnd, dataLength);

                double avgX = 0;
                double avgY = 0;
                int avgRangeStart = nextBucketStart;
                int avgRangeEnd = nextBucketEnd;
                int avgRangeLength = avgRangeEnd - avgRangeStart;

                for (int k = avgRangeStart; k < avgRangeEnd; k++)
                {
                    avgX += data[k].X;
                    avgY += data[k].Y;
                }
                if (avgRangeLength > 0)
                {
                    avgX /= avgRangeLength;
                    avgY /= avgRangeLength;
                }

                // Find the point in current bucket that forms the largest triangle with point a and the average point
                double maxArea = -1;
                int maxAreaIdx = -1;

                double ax = data[a].X;
                double ay = data[a].Y;

                for (int j = bucketStart; j < bucketEnd; j++)
                {
                    // Calculate triangle area
                    double area = Math.Abs((ax - avgX) * (data[j].Y - ay) - (ax - data[j].X) * (avgY - ay)) * 0.5;
                    if (area > maxArea)
                    {
                        maxArea = area;
                        maxAreaIdx = j;
                        nextA = j;
                    }
                }

                sampled.Add(data[maxAreaIdx]);
                a = nextA; // Move to next anchor
            }

            // Always include the last point
            sampled.Add(data[dataLength - 1]);

            return sampled;
        }
    }
}
