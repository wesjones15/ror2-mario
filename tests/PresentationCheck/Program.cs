using System;
using System.Reflection;
using MarioTest;
using RoR2;
using UnityEngine;
static class Program {
 static void Check(bool b,string message) { if(!b)throw new Exception(message); }
 static void Main() {
  var component=new MarioPresentation();
  typeof(MarioPresentation).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(component,null);
  Run.Begin();
  var ownMaster=new CharacterMaster{money=17};
  var mario=new CharacterBody{master=ownMaster,healthComponent=new HealthComponent()};
  var user=new LocalUser{currentNetworkUser=new NetworkUser(),cachedMaster=ownMaster,cachedBody=mario,
   cameraRigController=new CameraRigController{sceneCam=new Camera(),targetBody=mario}};
  LocalUserManager.first=user;
  MarioPresentation.Observe(user,mario); MarioDriver.Player=new MarioDriver(); MarioPresentation.Refresh();
  Check(MarioPresentation.Active && Native.created==0,"live Mario keeps his existing presentation");
  var teammate=new CharacterBody{master=new CharacterMaster{money=999},healthComponent=new HealthComponent()};
  MarioDriver.Player=null; user.cachedBody=null; user.cameraRigController.targetBody=teammate;
  MarioPresentation.Refresh();
  Check(MarioPresentation.Active && Native.created==1,"death keeps owner and opens independent HUD view");
  Check(MarioCoinHud.master==ownMaster && MarioLife.health==teammate.healthComponent,"spectating shows target health and own money");
  user.cameraRigController.targetBody=null; MarioPresentation.Refresh();
  Check(MarioLife.health==null && MarioLife.retainEmpty,"free spectator camera keeps an empty life meter");
  var drone=new CharacterBody{master=new CharacterMaster{money=23},healthComponent=new HealthComponent()};
  user.cachedBody=drone; user.cachedMaster=drone.master; user.cameraRigController.targetBody=drone;
  user.cameraRigController.sceneCam=new Camera(); MarioPresentation.Refresh();
  Check(MarioPresentation.Active && MarioPresentation.Camera==user.cameraRigController.sceneCam,"Remote Operation follows replacement camera");
  Check(MarioCoinHud.master==drone.master && MarioLife.health==drone.healthComponent && Native.created==1,"drone HUD uses controlled master without another allocation");
  user.cachedBody=mario;user.cachedMaster=ownMaster;MarioDriver.Player=new MarioDriver();
  MarioPresentation.Observe(user,mario);MarioPresentation.Refresh();
  Check(Native.created==1 && Native.freed==0,"Mario respawn reuses run presentation ownership");
  Run.End(); Check(!MarioPresentation.Active && Native.freed==1 && !MarioHudLayout.on,"run end frees HUD and restores layout");
  Run.Begin();MarioDriver.Player=null;
  Check(!MarioPresentation.Active,"new run cannot inherit previous Mario ownership");
  MarioPresentation.Observe(user,mario);MarioPresentation.Refresh();
  user.currentNetworkUser=new NetworkUser();MarioPresentation.Refresh();
  Check(!MarioPresentation.Active && Native.freed==2,"different network user cannot inherit effects");
  Console.WriteLine("PASS: live Mario, death/spectating, drone/camera replacement, own funds, respawn, run end and user replacement");
 }
}
