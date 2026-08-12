<img width="953" height="504" alt="Evil Laser" src="https://github.com/user-attachments/assets/ce58c695-da87-435e-9676-07cb9182bae9" />

The Problem
Native TakeDamage runs everything through ArmorProperties, damageAtRange, and calculates decimals that it summarizes into a netFireDamage. This net value often clamps 1,000,000 damage down into single digits before passing it over the network via DamageInfo structs.

Execution
I wrote the com.death.laser.mod to do the following:

The Hijack (UnitPart.TakeDamage): It dynamically intercepts the front-door of TakeDamage right before the Physics Engine reads it.
Signature Validation: It loops through incoming damage payload values. Because we force the Laser's FixedUpdate to fire out 1000000f every frame, if the patch sees any incoming raw parameter >900,000, it instantly engages the Custom Pipeline.
Physical Part Severance: When the pipeline activates, it skips the math entirely. It forcibly:

Sets health, localHealth, and hitPoints variables to 0.0f.
Forces ApplyDamage(1000000, 1000000, 1000000, 0) so any remaining scripts catch the death check.
Manually triggers Unity Detachment logic: DetachDamageParticles(), onJointBroken.DynamicInvoke(), and onPartDetached.DynamicInvoke(), physically tearing the part off visually before the central math can even finish processing.

Sincerely, Neutral Observer
