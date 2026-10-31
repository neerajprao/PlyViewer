// Reads what the viewer needs to place a .ply scene: the header (splat count, comments such as
// Brush's "Vertical axis") and a sample of splat positions for the scene's centre and size.
// Rendering itself is done by UnitySplats; this never loads the full file into memory.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PlyView
{
    public class PlyInfo
    {
        public int vertexCount;
        public Vector3? verticalAxis;                           // file coordinates, from "comment Vertical axis: x y z"
        public readonly List<Vector3> samples = new List<Vector3>(); // file coordinates

        const int MaxSamples = 200000;

        /// <summary>Returns null if the file is not a readable PLY.</summary>
        public static PlyInfo Read(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return Read(fs);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlyViewer] Could not read PLY header of {path}: {e.Message}");
                return null;
            }
        }

        static PlyInfo Read(FileStream fs)
        {
            var info = new PlyInfo();
            bool binaryLE = false, vertexIsFirst = false, inVertex = false, sawElement = false;
            int stride = 0, xOff = -1, yOff = -1, zOff = -1;
            bool xyzFloat = true;

            if (ReadLine(fs) != "ply") return null;
            while (true)
            {
                string line = ReadLine(fs);
                if (line == null) return null;
                if (line == "end_header") break;
                string[] p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                switch (p[0])
                {
                    case "format":
                        binaryLE = p.Length > 1 && p[1] == "binary_little_endian";
                        break;
                    case "comment":
                        ParseComment(line.Substring(7).Trim(), info);
                        break;
                    case "element":
                        inVertex = p.Length > 2 && p[1] == "vertex";
                        if (inVertex)
                        {
                            info.vertexCount = int.Parse(p[2], CultureInfo.InvariantCulture);
                            vertexIsFirst = !sawElement;
                        }
                        sawElement = true;
                        break;
                    case "property":
                        if (!inVertex || p.Length < 3) break;
                        if (p[1] == "list") { vertexIsFirst = false; break; }
                        int size = TypeSize(p[1]);
                        if (size == 0) { vertexIsFirst = false; break; }
                        string name = p[p.Length - 1];
                        bool isFloat = p[1] == "float" || p[1] == "float32";
                        if (name == "x") { xOff = stride; xyzFloat &= isFloat; }
                        else if (name == "y") { yOff = stride; xyzFloat &= isFloat; }
                        else if (name == "z") { zOff = stride; xyzFloat &= isFloat; }
                        stride += size;
                        break;
                }
            }

            // Positions are only sampled from plain 3DGS layouts (vertex element first, float x/y/z).
            // Compressed PLYs still render; the viewer then falls back to the renderer's bounds.
            if (!binaryLE || !vertexIsFirst || !xyzFloat || xOff < 0 || yOff < 0 || zOff < 0 ||
                info.vertexCount <= 0)
                return info;

            long dataStart = fs.Position;
            int step = Math.Max(1, info.vertexCount / MaxSamples);
            var buf = new byte[stride];
            for (long i = 0; i < info.vertexCount; i += step)
            {
                fs.Position = dataStart + i * stride;
                if (fs.Read(buf, 0, stride) != stride) break;
                var v = new Vector3(BitConverter.ToSingle(buf, xOff), BitConverter.ToSingle(buf, yOff),
                                    BitConverter.ToSingle(buf, zOff));
                if (float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z))
                    info.samples.Add(v);
            }
            return info;
        }

        static void ParseComment(string text, PlyInfo info)
        {
            const string key = "Vertical axis:";
            if (!text.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return;
            string[] v = text.Substring(key.Length).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (v.Length == 3 &&
                float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                new Vector3(x, y, z).sqrMagnitude > 1e-6f)
                info.verticalAxis = new Vector3(x, y, z).normalized;
        }

        static int TypeSize(string type)
        {
            switch (type)
            {
                case "char": case "uchar": case "int8": case "uint8": return 1;
                case "short": case "ushort": case "int16": case "uint16": return 2;
                case "int": case "uint": case "float": case "int32": case "uint32": case "float32": return 4;
                case "double": case "float64": return 8;
                default: return 0;
            }
        }

        static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = s.ReadByte()) != -1)
            {
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
                if (sb.Length > 4096) return null; // not a text header
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }
}
