﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using LeagueSharp;
using LeagueSharp.Common;
using ShineCommon.Maths;
using Geometry = ShineCommon.Maths.Geometry;
using SharpDX;

namespace ShineCommon
{
    public struct EvadeData
    {
        public Vector2 Position;
        public bool IsTargetted;
        public bool IsSelfCast;
        public Obj_AI_Base Target;

        public EvadeData(Vector2 v, bool bl1, bool bl2, Obj_AI_Base obj)
        {
            Position = v;
            IsTargetted = bl1;
            IsSelfCast = bl2;
            Target = obj;
        }
    }
    public class Evader
    {
        private EvadeMethods SpecialMethod;
        private Spell EvadeSpell;
        private Menu evade;
        private Thread m_evade_thread;

        public ObjectPool<DetectedSpellData> m_spell_pool = new ObjectPool<DetectedSpellData>(() => new DetectedSpellData());
        public ConcurrentQueue<DetectedSpellData> m_spell_queue = new ConcurrentQueue<DetectedSpellData>();
        public ConcurrentQueue<EvadeData> m_evade_queue = new ConcurrentQueue<EvadeData>();

        public object m_lock;

        public Evader(out Menu _evade, EvadeMethods method = EvadeMethods.None, Spell spl = null)
        {
            SpecialMethod = method;
            EvadeSpell = spl;
            evade = new Menu("Evade", "Evade");
            evade.AddItem(new MenuItem("EVADEENABLE", "Enabled").SetValue(false));
            foreach (var enemy in HeroManager.Enemies)
            {
                foreach (var spell in SpellDatabase.EvadeableSpells.Where(p => p.ChampionName == enemy.ChampionName && p.EvadeMethods.HasFlag(method)))
                {
                    evade.AddItem(new MenuItem(spell.SpellName, String.Format("{0} ({1})", spell.ChampionName, spell.Slot)).SetValue(true));
                    evade.Item("EVADEENABLE").SetValue(true);
                }
            }
            _evade = evade;
            m_evade_thread = new Thread(new ThreadStart(EvadeThread));
            m_evade_thread.Start();
            Game.OnUpdate += Game_OnUpdate;
            Obj_AI_Base.OnProcessSpellCast += Obj_AI_Base_OnProcessSpellCast;
        }

        public void SetEvadeSpell(Spell spl)
        {
            EvadeSpell = spl;
        }

        private void Obj_AI_Base_OnProcessSpellCast(Obj_AI_Base sender, GameObjectProcessSpellCastEventArgs args)
        {
            if (evade != null && evade.Item("EVADEENABLE").GetValue<bool>() && sender.Type == GameObjectType.obj_AI_Hero && sender.IsEnemy)
            {
                Vector2 sender_pos = sender.Position.To2D();
                var item = evade.Items.FirstOrDefault(q => q.Name == args.SData.Name);
                if (item != null && item.GetValue<bool>())
                {
                    var spell = SpellDatabase.EvadeableSpells.FirstOrDefault(p => p.SpellName == args.SData.Name);
                    if (spell != null)
                    {
                        if (spell.IsSkillshot)
                        {
                            DetectedSpellData dcspell = m_spell_pool.GetObject();
                            dcspell.Set(spell, sender_pos, args.End.To2D(), sender, args);
                            m_spell_queue.Enqueue(dcspell);
                        }
                    }
                }
                if (item == null && args.Target != null && args.Target.IsMe && args != null && args.SData != null && !args.SData.IsAutoAttack())
                    OnSpellHitDetected(null, sender_pos, ObjectManager.Player);
            }
        }

        private void Game_OnUpdate(EventArgs args)
        {
            if (ObjectManager.Player.IsDead || args == null)
                return;

            EvadeData edata;
            if (m_evade_queue.TryDequeue(out edata))
            {
                Console.WriteLine("try evade with data Targetted: {0}, SelfCast: {1}, TargetName: {2}", edata.IsTargetted, edata.IsSelfCast, edata.Target.Name);
                if (EvadeSpell.IsReady())
                {
                    if (edata.IsSelfCast)
                        EvadeSpell.Cast();
                    else if (edata.IsTargetted && edata.Target != null)
                        EvadeSpell.Cast(edata.Target);
                    else
                        EvadeSpell.Cast(edata.Position);
                }
            }
        }

        public void OnSpellHitDetected(SpellData spell, Vector2 direction, Obj_AI_Base target)
        {
            EvadeData edata;
            if (spell == null) //direct cast
            {
                //
            }
            else
            {
                Vector2 evade_direction = direction.Perpendicular();
                Vector2 evade_pos = ObjectManager.Player.Position.To2D() + direction.Perpendicular() * EvadeSpell.Range;
                edata = new EvadeData(evade_pos, SpecialMethod.HasFlag(EvadeMethods.MorganaE), SpecialMethod.HasFlag(EvadeMethods.SivirE), target);
                m_evade_queue.Enqueue(edata);
            }
        }

        public void EvadeThread()
        {
            //TO DO: collision check (minion, champ, yasuo wall)
            DetectedSpellData dcspell;
            while (true)
            {
                if (m_spell_queue.TryDequeue(out dcspell))
                {
                    Vector2 my_pos = ObjectManager.Player.Position.To2D();
                    Vector2 sender_pos = dcspell.StartPosition;
                    Vector2 end_pos = dcspell.EndPosition;
                    Vector2 direction = (end_pos - sender_pos).Normalized();
                    if (sender_pos.Distance(end_pos) > dcspell.Spell.Range)
                        end_pos = sender_pos + direction * dcspell.Spell.Range;

                    Geometry.Polygon my_hitbox = ClipperWrapper.DefineRectangle(my_pos - 60, my_pos + 60, 60);
                    Geometry.Polygon spell_hitbox = null;
                    if (dcspell.Spell.Type == SkillshotType.SkillshotLine)
                    {
                        spell_hitbox = ClipperWrapper.DefineRectangle(sender_pos, end_pos, dcspell.Spell.Radius);
                    }
                    else if (dcspell.Spell.Type == SkillshotType.SkillshotCircle)
                    {
                        spell_hitbox = ClipperWrapper.DefineCircle(end_pos, dcspell.Spell.Radius);
                    }
                    else if (dcspell.Spell.Type == SkillshotType.SkillshotCone)
                    {
                        spell_hitbox = ClipperWrapper.DefineSector(sender_pos, end_pos - sender_pos, dcspell.Spell.Radius * (float)Math.PI / 180, dcspell.Spell.Range);
                    }
                    
                    if (spell_hitbox != null)
                    {
                        if (ClipperWrapper.IsIntersects(ClipperWrapper.MakePaths(my_hitbox), ClipperWrapper.MakePaths(spell_hitbox)))
                            OnSpellHitDetected(dcspell.Spell, direction, ObjectManager.Player);
                        else
                        {
                            if (SpecialMethod.HasFlag(EvadeMethods.MorganaE))
                            {
                                foreach (Obj_AI_Base ally in ObjectManager.Player.GetAlliesInRange(EvadeSpell.Range))
                                {
                                    Vector2 ally_pos = ally.ServerPosition.To2D();
                                    Geometry.Polygon ally_hitbox = ClipperWrapper.DefineRectangle(ally_pos, ally_pos + 60, 60);
                                    if (ClipperWrapper.IsIntersects(ClipperWrapper.MakePaths(ally_hitbox), ClipperWrapper.MakePaths(spell_hitbox)))
                                    {
                                        OnSpellHitDetected(dcspell.Spell, direction, ally);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    m_spell_pool.PutObject(dcspell);
                }
                Thread.Sleep(1);
            }
        }
    }
}
