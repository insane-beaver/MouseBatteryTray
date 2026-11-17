using System;
using System.Collections.Generic;
using System.Drawing;

namespace MouseBatteryTray
{
    // Hand-made pixel glyphs on a 16x16 grid: crisp at tray size, scaled by whole pixels on high DPI
    static class IconArt
    {
        static readonly Dictionary<char, string[]> Big = new Dictionary<char, string[]>
        {
            ['0'] = new[] { ".#####.", "##...##", "##...##", "##...##", "##...##", "##...##", "##...##", "##...##", "##...##", ".#####." },
            ['1'] = new[] { "...##..", "..###..", ".####..", "...##..", "...##..", "...##..", "...##..", "...##..", "...##..", "...##.." },
            ['2'] = new[] { ".#####.", "##...##", ".....##", ".....##", "....##.", "...##..", "..##...", ".##....", "##.....", "#######" },
            ['3'] = new[] { ".#####.", "##...##", ".....##", ".....##", "..####.", ".....##", ".....##", ".....##", "##...##", ".#####." },
            ['4'] = new[] { "....##.", "...###.", "..####.", ".##.##.", "##..##.", "##..##.", "#######", "....##.", "....##.", "....##." },
            ['5'] = new[] { "#######", "##.....", "##.....", "######.", ".....##", ".....##", ".....##", ".....##", "##...##", ".#####." },
            ['6'] = new[] { ".#####.", "##.....", "##.....", "######.", "##...##", "##...##", "##...##", "##...##", "##...##", ".#####." },
            ['7'] = new[] { "#######", ".....##", ".....##", "....##.", "....##.", "...##..", "...##..", "..##...", "..##...", "..##..." },
            ['8'] = new[] { ".#####.", "##...##", "##...##", "##...##", ".#####.", "##...##", "##...##", "##...##", "##...##", ".#####." },
            ['9'] = new[] { ".#####.", "##...##", "##...##", "##...##", "##...##", ".######", ".....##", ".....##", ".....##", ".#####." },
        };

        // narrow digits, only for "100"
        static readonly Dictionary<char, string[]> Small = new Dictionary<char, string[]>
        {
            ['0'] = new[] { ".###.", "##.##", "##.##", "##.##", "##.##", "##.##", "##.##", "##.##", ".###." },
            ['n'] = new[] { ".##", "###", ".##", ".##", ".##", ".##", ".##", ".##", ".##" },
        };

        static readonly string[] Percent = { "#...#", "...#.", "..#..", ".#...", "#...#" };
        static readonly string[] Dash = { "#####", "#####" };
        static readonly string[] Bang = { "##", "##", "##", "##", "##", "..", "##" };

        public static Bitmap Draw(int size, string text, Color color)
        {
            int k = Math.Max(1, size / 16);
            int off = (size - 16 * k) / 2;
            var bmp = new Bitmap(size, size);
            void Put(string[] g, int x, int y)
            {
                for (int r = 0; r < g.Length; r++)
                    for (int c = 0; c < g[r].Length; c++)
                        if (g[r][c] == '#')
                            for (int dy = 0; dy < k; dy++)
                                for (int dx = 0; dx < k; dx++)
                                    bmp.SetPixel(off + (x + c) * k + dx, off + (y + r) * k + dy, color);
            }

            if (!int.TryParse(text, out int v) || v < 0 || v > 100)
            {
                if (text == "!") Put(Bang, 7, 5); else Put(Dash, 5, 8);
                return bmp;
            }

            Put(Percent, 11, 0);
            if (v == 100) { Put(Small['n'], 1, 7); Put(Small['0'], 5, 7); Put(Small['0'], 11, 7); }
            else if (v < 10) Put(Big[text[0]], 5, 6);
            else { Put(Big[text[0]], 1, 6); Put(Big[text[1]], 9, 6); }
            return bmp;
        }
    }
}
