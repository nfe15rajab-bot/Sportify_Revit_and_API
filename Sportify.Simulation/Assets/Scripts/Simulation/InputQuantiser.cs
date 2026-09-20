#nullable disable
using System;
using System.Collections;
using System.Reflection;

namespace Sportify.Simulation
{
    /// <summary>
    /// One number, read from the same layout by the two readers (Unity's, which gets a float from JsonUtility, and the add-in's, which gets a double from
    /// System.Text.Json), differs in its seventh digit: 67.6 is 67.5999985 as a float. Every analysis is deterministic, so that noise can flip a rounding in a
    /// sentence ("Column 13 at (59.1, 7)" against "(59.2, 7)"), a tie between two pieces of advice, or a threshold. Both readers therefore round what they read
    /// to six significant digits before any analysis sees it: a value typed with six digits or fewer (all of them, in practice) then comes out identical on
    /// both sides, and a longer one is cut to the same six. Checked by Tools/ReaderParity on every fixture.
    /// </summary>
    public static class InputQuantiser
    {
        /// <summary>Six significant digits.</summary>
        public static double Q(double v)
        {
            if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
            var digits = 5 - (int)Math.Floor(Math.Log10(Math.Abs(v)));
            if (digits > 15) digits = 15;
            if (digits < -8) return v;
            return digits >= 0 ? Math.Round(v, digits) : Math.Round(v / Math.Pow(10, -digits)) * Math.Pow(10, -digits);
        }

        public static double Q(float v) { return Q((double)v); }

        /// <summary>
        /// A text with a fallback for "not given". JsonUtility hands a string missing from the file over as "" (never null), so a reader that says <c>label ?? "Sports field 1"</c>
        /// never reaches its fallback in Unity. Both readers use this instead, so an absent and an empty text are the same to them.
        /// </summary>
        public static string Or(string value, string fallback) { return string.IsNullOrEmpty(value) ? fallback : value; }

        /// <summary>Rounds every double an input object holds (fields, arrays, lists, nested objects), in place, and returns it.</summary>
        public static T Apply<T>(T inputs) where T : class
        {
            Walk(inputs, 0);
            return inputs;
        }

        static void Walk(object obj, int depth)
        {
            if (obj == null || depth > 12) return;
            var type = obj.GetType();
            if (type.IsPrimitive || type == typeof(string) || type.IsEnum) return;

            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsInitOnly) continue;
                var ft = f.FieldType;
                var value = f.GetValue(obj);
                if (value == null) continue;
                if (ft == typeof(double)) { f.SetValue(obj, Q((double)value)); continue; }
                if (ft == typeof(double?)) { f.SetValue(obj, (double?)Q(((double?)value).Value)); continue; }
                if (ft == typeof(double[])) { var a = (double[])value; for (var i = 0; i < a.Length; i++) a[i] = Q(a[i]); continue; }
                if (ft.IsPrimitive || ft == typeof(string) || ft.IsEnum) continue;
                if (value is IList list)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var item = list[i];
                        if (item is double d) list[i] = Q(d);
                        else if (item is double[] arr) { for (var k = 0; k < arr.Length; k++) arr[k] = Q(arr[k]); }
                        else if (item != null) Walk(item, depth + 1);
                    }
                    continue;
                }
                Walk(value, depth + 1);
            }
        }
    }
}
