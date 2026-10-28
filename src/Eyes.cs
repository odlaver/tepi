using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Tepi
{
    enum EyeMood { None, Happy, Surprised, Sleepy, Glance }

    // Idle eyes: blink, look, short moods
    partial class Island
    {
        // Eye size, spacing, gaze reach at 1x
        const float EyeW = 6f, EyeH = 9f, EyeGap = 9.5f, EyeReachX = 3f, EyeReachY = 1.5f;
        const float EyeSenseFps = 10, EyeNear = 700, MoodSeconds = 1.5f, MoodOut = 0.3f;
        readonly Spring lookX = new Spring(0, 120, 20, 0.04f), lookY = new Spring(0, 120, 20, 0.04f), eyeLid = new Spring(0, 60, 15, 0.01f);
        readonly Random eyeRandom = new Random();
        readonly GraphicsPath eyeShape = new GraphicsPath();
        readonly PointF[] eyeAt = new PointF[1];
        DateTime blinkUtc = DateTime.MinValue, eyeStepUtc, saccadeUtc = DateTime.MinValue, saccadeBackUtc, moodUtc, moodQueuedUtc, drowsyAtUtc;
        bool blinkTwice, saccading, drowsy, eyesNear, packEyes = true;
        float saccadeX, saccadeY, eyeCx, eyeCy, eyeK = 1;
        int moodSide, gazeMode, miniEyeSig;
        EyeMood mood, moodQueued;
        Toast moodToast;
        PointF eyeScreen;
        Func<int> eyeSense;
        Action<DateTime> eyeQuiet;
        Func<DateTime, double> eyePaint;

        // Setting on, theme pack allows
        bool EyesOn { get { return packEyes && settings.GetBool("island.eyes", true); } }

        bool EyesFollow { get { return !Anim.Reduced && settings.GetBool("island.follow", true); } }

        // Centred at k size; redraws alone
        void DrawEyes(float cx, float cy, float k)
        {
            if (eyePaint == null)
            {
                eyeSense = EyesSense;
                eyeQuiet = delegate { };
                eyePaint = PaintEyes;
            }
            eyeCx = cx;
            eyeCy = cy;
            eyeK = k;
            eyeAt[0] = new PointF(cx, cy);
            g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, eyeAt);
            eyeScreen = new PointF(WindowLeft(snapPxW) + eyeAt[0].X, monitorPx.Top + eyeAt[0].Y);
            float rx = (EyeGap / 2 + EyeW * 1.3f + EyeReachX) * k + 1.5f, ry = (EyeH * 0.6f + EyeReachY) * k + 1.5f;
            var r = new RectangleF(cx - rx, cy - ry, rx * 2, ry * 2);
            // Pointer sensor; Mini polls in MiniTick
            if (EyesFollow && current != View.Mini) LiveRect(r, EyeSenseFps, eyeSense, eyeQuiet);
            LiveRect(r, eyePaint);
        }

        // Quantized gaze; changes wake the eyes
        int EyesSense()
        {
            Native.POINT c;
            bool down;
            ReadPointer(out c, out down);
            return SenseAt(c);
        }

        int SenseAt(Native.POINT c)
        {
            float tx, ty;
            if (!AimAt(c, out tx, out ty)) return int.MinValue;
            return (int)Math.Round(tx * 3) * 1000 + (int)Math.Round(ty * 3);
        }

        // Mini: dwell poll feeds the gaze
        void MiniEyesSense(Native.POINT c)
        {
            if (eyePaint == null || !EyesOn || !EyesFollow) return;
            int s = SenseAt(c);
            if (s == miniEyeSig) return;
            miniEyeSig = s;
            LiveWake(eyePaint);
        }

        // Pointer within reach: gaze offset in px
        bool EyesAim(out float tx, out float ty)
        {
            Native.POINT c;
            bool down;
            ReadPointer(out c, out down);
            return AimAt(c, out tx, out ty);
        }

        bool AimAt(Native.POINT c, out float tx, out float ty)
        {
            tx = ty = 0;
            float dx = (c.X - eyeScreen.X) / scale, dy = (c.Y - eyeScreen.Y) / scale;
            if (dx * dx + dy * dy > EyeNear * EyeNear) return false;
            tx = EyeReachX * dx / (Math.Abs(dx) + 80);
            ty = EyeReachY * dy / (Math.Abs(dy) + 50);
            return true;
        }

        double Between(double a, double b) { return a + (b - a) * eyeRandom.NextDouble(); }

        // Gaze springs: snappy saccade, slow drift, tracking
        void Gaze(int mode)
        {
            if (mode == gazeMode) return;
            gazeMode = mode;
            float response = mode == 1 ? 0.12f : mode == 2 ? 0.45f : 0.24f, damping = mode == 2 ? 1f : 0.88f;
            Springs.Tune(lookX, response, damping);
            Springs.Tune(lookY, response, damping);
        }

        // Live callback: seconds to next redraw
        double PaintEyes(DateTime now)
        {
            float dt = (float)Math.Min(0.25, Math.Max(0, (now - eyeStepUtc).TotalSeconds));
            eyeStepUtc = now;
            bool reduced = Anim.Reduced;
            float k = MoodMix(now);
            bool sleepy = !reduced && (Drowsy(now) || (mood == EyeMood.Sleepy && k > 0));
            float tx = 0, ty = 0;
            eyesNear = !reduced && EyesFollow && EyesAim(out tx, out ty);
            if (eyesNear) { saccading = false; Gaze(0); }
            // Mini: no idle glances, fewer frames
            else if (!reduced && !sleepy && current != View.Mini) Saccade(now, out tx, out ty);
            else { tx = ty = 0; Gaze(2); }
            // Moods and the bubble steer gaze
            if (k > 0 && moodSide != 0) { tx = moodSide * EyeReachX; ty = 0.3f; Gaze(0); }
            if (!reduced && !bubbleRect.IsEmpty) tx = EyeReachX;
            lookX.Target = tx;
            lookY.Target = ty;
            lookX.Step(dt);
            lookY.Step(dt);
            eyeLid.Target = sleepy ? 1 : 0;
            eyeLid.Step(dt);
            float close = BlinkClose(now, sleepy);
            bool still = lookX.Settled && lookY.Settled && eyeLid.Settled && close <= 0 && !MoodMoving(now);
            PaintEyePair(eyeCx + lookX.Value * eyeK, eyeCy + lookY.Value * eyeK, close, mood == EyeMood.Happy ? k : 0, mood == EyeMood.Surprised ? k : 0,
                         Ease.Clamp01(eyeLid.Value), Math.Abs(lookX.Value) / EyeReachX, still, p.T.Text);
            if (!still) return EyesPace(now, close);
            // At rest: wake for next event
            double wait = (blinkUtc - now).TotalSeconds;
            if (!eyesNear && !reduced && !sleepy && current != View.Mini) wait = Math.Min(wait, ((saccading ? saccadeBackUtc : saccadeUtc) - now).TotalSeconds);
            if (mood != EyeMood.None) wait = Math.Min(wait, (moodUtc - now).TotalSeconds + (wait < 0 ? 0 : MoodSeconds));
            return Math.Max(1 / LiveFps, wait);
        }

        // Blinks at 30 fps; drifts by half-pixels
        double EyesPace(DateTime now, float close)
        {
            if (close > 0 || !eyeLid.Settled || MoodMoving(now)) return 1 / LiveFps;
            float v = Math.Max(Math.Abs(lookX.Velocity), Math.Abs(lookY.Velocity)) * scale * eyeK;
            return Math.Min(0.2, Math.Max(1 / LiveFps, 0.5 / Math.Max(0.01f, v)));
        }

        // Idle glances that drift back
        void Saccade(DateTime now, out float tx, out float ty)
        {
            if (saccadeUtc == DateTime.MinValue) saccadeUtc = now.AddSeconds(Between(5, 10));
            if (!saccading && now >= saccadeUtc)
            {
                saccading = true;
                saccadeBackUtc = now.AddSeconds(Between(0.6, 1.4));
                saccadeX = (float)Between(-2.4, 2.4);
                saccadeY = (float)Between(-0.9, 0.9);
                Gaze(1);
            }
            else if (saccading && now >= saccadeBackUtc)
            {
                saccading = false;
                saccadeUtc = now.AddSeconds(Between(6, 12));
                Gaze(2);
            }
            tx = saccading ? saccadeX : 0;
            ty = saccading ? saccadeY : 0;
        }

        // 0 open, 1 shut; eased both ways
        float BlinkClose(DateTime now, bool slow)
        {
            if (blinkUtc == DateTime.MinValue) NextBlink(now, slow);
            bool reduced = Anim.Reduced;
            float shut = slow ? 0.16f : reduced ? 0.11f : 0.07f, hold = slow ? 0.12f : reduced ? 0.06f : 0.04f, open = slow ? 0.26f : reduced ? 0.18f : 0.12f;
            float one = shut + hold + open, gap = 0.09f;
            double t = (now - blinkUtc).TotalSeconds;
            if (t < 0) return 0;
            if (t >= (blinkTwice ? one * 2 + gap : one)) { NextBlink(now, slow); return 0; }
            if (blinkTwice && t >= one)
            {
                t -= one + gap;
                if (t < 0) return 0;
            }
            if (t < shut) { float u = (float)(t / shut); return u * u * u; }
            if (t < shut + hold) return 1;
            float v = 1 - (float)((t - shut - hold) / open);
            return v * v * v;
        }

        void NextBlink(DateTime now, bool slow)
        {
            bool reduced = Anim.Reduced;
            blinkUtc = now.AddSeconds(slow ? Between(3, 6) : reduced ? Between(4, 8) : Between(2.5, 6));
            blinkTwice = !slow && !reduced && eyeRandom.NextDouble() < 0.18;
        }

        // Long without input, or late at night
        bool Drowsy(DateTime now)
        {
            if ((now - drowsyAtUtc).TotalSeconds < 1) return drowsy;
            drowsyAtUtc = now;
            int hour = DateTime.Now.Hour;
            drowsy = hour >= 23 || hour < 5 || IdleSeconds() > 300;
            return drowsy;
        }

        // Mood strength: springs in, holds, fades out
        float MoodMix(DateTime now)
        {
            if (mood == EyeMood.None) return 0;
            float t = (float)(now - moodUtc).TotalSeconds;
            if (t < 0) return 0;
            if (t >= MoodSeconds + MoodOut) { mood = EyeMood.None; moodSide = 0; return 0; }
            float k = Anim.Reduced ? Ease.Clamp01(t / 0.15f) : Springs.Progress(t, 0.32f, mood == EyeMood.Surprised ? 0.55f : 0.9f);
            if (t > MoodSeconds) k *= 1 - Ease.OutCubic((t - MoodSeconds) / MoodOut);
            return k;
        }

        bool MoodMoving(DateTime now)
        {
            if (mood == EyeMood.None) return false;
            float t = (float)(now - moodUtc).TotalSeconds;
            return t >= 0 && (t < 0.6f || t > MoodSeconds);
        }

        // Queue a mood for the eyes
        void EyesReact(EyeMood m, int side)
        {
            if (!EyesOn || Anim.Reduced) return;
            moodQueued = m;
            moodSide = side;
            moodQueuedUtc = DateTime.UtcNow;
            if (current != View.Mini) return;
            EyesShown();
            Kick();
        }

        // Idle view just appeared
        void EyesShown()
        {
            if (moodQueued == EyeMood.None) return;
            var m = moodQueued;
            moodQueued = EyeMood.None;
            if ((DateTime.UtcNow - moodQueuedUtc).TotalSeconds > 20) return;
            // No smile for a failed run
            var s = moodToast != null ? moodToast.Data as AgentSession : null;
            moodToast = null;
            if (m == EyeMood.Happy && s != null && s.Failed) return;
            mood = m;
            moodUtc = DateTime.UtcNow.AddSeconds(0.25);
        }

        // Toasts that the face reacts to afterwards
        void EyesToast(View v, Toast t)
        {
            if (v == View.Done) { EyesReact(EyeMood.Happy, 0); moodToast = t; }
            else if (v == View.Notif) EyesReact(EyeMood.Surprised, 1);
            else if (v == View.Privacy) EyesReact(EyeMood.Glance, -1);
        }

        // Pair with parallax, one shape language
        void PaintEyePair(float cx, float cy, float close, float happy, float wow, float lid, float side, bool snap, Color c)
        {
            float half = (EyeGap + EyeW) / 2 * eyeK * (1 - 0.06f * Ease.Clamp01(side));
            PaintEye(cx - half, cy, close, happy, wow, lid, snap, c);
            PaintEye(cx + half, cy, close, happy, wow, lid, snap, c);
        }

        void PaintEye(float x, float cy, float close, float happy, float wow, float lid, bool snap, Color c)
        {
            float w = EyeW * eyeK * (1 + 0.27f * wow), h = EyeH * eyeK * (1 + 0.16f * wow);
            // Blink folds toward a point below centre
            float hv = Math.Max(0.7f * eyeK, h * (1 - 0.92f * close) * (1 - 0.72f * happy));
            float top = cy - h / 2 + (h - hv) * 0.62f, left = x - w / 2;
            if (snap)
            {
                // Resting: edges on device pixels
                left = (float)Math.Round(left * scale) / scale;
                w = (float)Math.Round(w * scale) / scale;
                top = (float)Math.Round(top * scale) / scale;
            }
            float alpha = p.Alpha;
            if (happy < 0.99f)
            {
                p.Alpha = alpha * (1 - happy);
                float lidY = cy - h / 2 + lid * h * 0.5f;
                if (lid > 0.02f && lidY > top) Lidded(new RectangleF(left, lidY, w, top + hv - lidY), lid, c);
                else p.FillRound(new RectangleF(left, top, w, hv), Math.Min(w, hv) / 2, c);
            }
            if (happy > 0.01f)
            {
                // Smiling eye: an arch, round caps
                p.Alpha = alpha * happy;
                float rw = w * 0.62f;
                g.DrawArc(p.P(c, Math.Max(1.3f, 2f * eyeK)), x - rw, cy - rw * 0.55f + 1.4f * eyeK * (1 - happy), rw * 2, rw * 1.7f, 200, 140);
            }
            p.Alpha = alpha;
        }

        // Flat top under a lowered lid
        void Lidded(RectangleF r, float lid, Color c)
        {
            if (r.Height < 0.5f) return;
            float bottom = Math.Min(r.Width, r.Height * 2) / 2, top = Math.Min(r.Height / 2, r.Width / 2 * (1 - lid));
            eyeShape.Reset();
            if (top > 0.25f)
            {
                eyeShape.AddArc(r.X, r.Y, top * 2, top * 2, 180, 90);
                eyeShape.AddArc(r.Right - top * 2, r.Y, top * 2, top * 2, 270, 90);
            }
            else eyeShape.AddLine(r.X, r.Y, r.Right, r.Y);
            eyeShape.AddArc(r.Right - bottom * 2, r.Bottom - bottom * 2, bottom * 2, bottom * 2, 0, 90);
            eyeShape.AddArc(r.X, r.Bottom - bottom * 2, bottom * 2, bottom * 2, 90, 90);
            eyeShape.CloseFigure();
            g.FillPath(p.B(c), eyeShape);
        }
    }
}
