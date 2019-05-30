using System;
using System.Collections.Generic;
using System.Text;

class UnitTests
{
    public bool RunUnitTests()
    {
        bool result1 = UnitTest1();
        bool result2 = UnitTest2();

        return result1 || result2;
    }

    bool UnitTest1()
    {
        long[,] difftestshours = new long[,]
        {
             { 0,  0,  0 },
             { 0,  1,  1 },
             { 0,  11, 11 },
             { 0,  12, 12 },
             { 0,  13, -11 },
             { 0,  23, -1 },
             { 23, 0,  1 },
             { 23, 1,  2 },
             { 23, 11, 12 },
             { 23, 12, -11 },
             { 23, 13, -10 },
             { 23, 23, 0 },
             { 3,  4,  1 },
             { 3,  2,  -1 },
             { 3,  23, -4 },
             { 3,  14, 11 },
             { 3,  15, 12 },
             { 3,  16, -11 }
        };


        Log($"{difftestshours.Length}");

        for (int i = 0; i < difftestshours.Length / 3; i++)
        {
            long timeSinceMidnightMs = difftestshours[i, 0];
            long desiredTimeOfDayMs = difftestshours[i, 1];
            long timediffms = difftestshours[i, 2];
            long resultms = ArtilleryResult.GetDiff(timeSinceMidnightMs * 3600 * 1000, desiredTimeOfDayMs * 3600 * 1000);

            if (resultms != timediffms * 3600 * 1000)
            {
                Log($"ERROR: {timeSinceMidnightMs} {desiredTimeOfDayMs} was: {resultms / 3600 / 1000} should be: {timediffms}");
            }
            else
            {
                Log($"OK:    {timeSinceMidnightMs} {desiredTimeOfDayMs} was {resultms / 3600 / 1000}");
            }
        }

        return true;
    }

    bool UnitTest2()
    {
        var dic = new Dictionary<int, int>
        {
            [11] = 2,
            [3] = 3
        };

        var results = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            results.Add(ArtilleryResult.PopSmallestKey(dic));
        }
        var compare = new[] { 3, 3, 3, 11, 11, 0 };

        bool error = false;

        for (int i = 0; i < 6; i++)
        {
            if (results[i] != compare[i])
            {
                error = true;
                Log($"ERROR: Expected {compare[i]}, was {results[i]}");
            }
            else
            {
                Log($"OK: Expected {compare[i]}, was {results[i]}");
            }
        }

        return error;
    }

    static void Log(string message)
    {
        Console.WriteLine(message);
    }
}
