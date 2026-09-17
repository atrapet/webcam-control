# Webcam Control

Panneau de contrôle alternatif pour webcam UVC sous Windows, écrit parce que les
utilitaires constructeurs sont pénibles et que les réglages ne tiennent pas.

Développé et validé sur une **NexiGo N60 FHD**, mais rien n'est spécifique à ce
modèle : l'application interroge la caméra au démarrage et n'affiche que les
réglages qu'elle expose réellement, avec ses bornes réelles.

![Interface](docs/screenshot.png)

## Pourquoi pas un « vrai » driver

La NexiGo N60 n'a pas de pilote constructeur : Windows la gère avec son pilote de
classe UVC générique, `usbvideo.sys`, à jour et parfaitement fonctionnel.

```
FriendlyName : NexiGo N60 FHD Webcam
InstanceId   : USB\VID_3443&PID_60BB&MI_00
Service      : usbvideo
Mfg          : @usbvideo.inf,%msft%;Microsoft
```

Écrire un pilote noyau de remplacement n'apporterait donc rien, coûterait une
signature WHQL et ferait perdre la compatibilité. Tout le contrôle utile d'une
caméra UVC passe par l'espace utilisateur, via les interfaces DirectShow
`IAMVideoProcAmp` et `IAMCameraControl`. C'est ce que fait cette application.

## Ce que la caméra expose

Relevé fait par l'application elle-même sur la N60 :

| Réglage | Interface | Plage | Défaut | Auto |
|---|---|---|---|---|
| Exposition | CameraControl | −14 … −1 (2^n secondes) | −7 | oui |
| Gain | VideoProcAmp | 0 … 100 | 5 | non |
| Balance des blancs | VideoProcAmp | 2600 … 6500 K | 4650 | oui |
| Luminosité | VideoProcAmp | 0 … 255 | 128 | non |
| Contraste | VideoProcAmp | 0 … 255 | 128 | non |
| Saturation | VideoProcAmp | 0 … 255 | 128 | non |
| Netteté | VideoProcAmp | 0 … 255 | 128 | non |
| Zoom | CameraControl | 10 … 20 | 10 | non |
| Panoramique | CameraControl | −10 … +10 | 0 | non |
| Inclinaison | CameraControl | −10 … +10 | 0 | non |

Pas de mise au point pilotable (objectif à focale fixe), pas de teinte, pas de
gamma, pas de compensation de contre-jour : ces propriétés ne sont pas
implémentées par le firmware et l'application ne les affiche donc pas.

Formats de capture disponibles : MJPEG jusqu'à 1920x1080 à 30 i/s,
YUY2 limité à 1280x720 à 10 i/s.

## Fonctionnalités

- **Tous les réglages UVC en curseurs**, découverts dynamiquement.
- **Profils nommés** enregistrés en JSON, commutables depuis la fenêtre ou
  directement depuis la zone de notification.
- **Verrouiller les auto** : fige d'un clic l'exposition et la balance des blancs
  sur la valeur où l'automatique les a amenées. C'est le remède à l'image qui
  pompe et aux couleurs qui virent.
- **Auto-exposition adoucie** : une auto-exposition logicielle qui corrige au
  gain plutôt qu'à l'exposition, donc sans les sauts du firmware. Voir la
  section dédiée plus bas.
- **Watchdog** : toutes les N secondes, l'application relit la caméra et
  réapplique ce qui a dérivé. Les réglages ne se perdent plus quand une
  application les écrase ou quand la caméra sort de veille.
- **Reprise après rebranchement** : `WM_DEVICECHANGE` déclenche une reconnexion
  et une réapplication du profil.
- **Aperçu live** avec grille des tiers et pavé de cadrage panoramique / inclinaison / zoom.
- **Démarrage automatique** en zone de notification.
- **Mode ligne de commande** pour scripter l'application d'un profil.

## Compilation

Aucune dépendance, aucun SDK à installer : on utilise le compilateur C# livré
avec le .NET Framework, présent sur toute installation de Windows.

```powershell
.\build.ps1          # produit bin\WebcamControl.exe
.\build.ps1 -Run     # compile puis lance
```

L'icône de l'application est reconstruite par le build à partir de
`tools\MakeIcon.cs`, plutôt que versionnée en binaire.

Pour la rendre trouvable dans la recherche Windows :

```powershell
.\tools\install-shortcut.ps1          # raccourci dans le menu Démarrer
.\tools\install-shortcut.ps1 -Tray    # démarre directement en zone de notification
.\tools\install-shortcut.ps1 -Remove  # retire le raccourci
```

Le raccourci est créé pour l'utilisateur courant, sans droits administrateur.

## Utilisation

```
WebcamControl.exe                    ouvre le panneau
WebcamControl.exe --tray             démarre réduit dans la zone de notification
WebcamControl.exe --list             liste les caméras et les profils
WebcamControl.exe --apply "Visio"    applique un profil puis rend la main
```

Fermer la fenêtre réduit l'application dans la zone de notification ; le
watchdog continue de tourner. « Quitter » depuis le menu de l'icône arrête tout.

La configuration est dans `%APPDATA%\WebcamControl\config.json`, le journal dans
`%APPDATA%\WebcamControl\webcam-control.log`. Les deux sont lisibles et
modifiables à la main.

## Auto-exposition adoucie

L'auto-exposition du firmware fait varier la luminosité par paliers visibles.
Le mode « auto-exposition adoucie » la remplace par une boucle logicielle
nettement plus douce. Sa conception découle entièrement de quatre mesures
faites sur la caméra, qui valent d'être connues avant de vouloir l'améliorer.

**1. L'exposition est un réglage grossier, le gain un réglage fin.**
Balayage à luminance mesurée (YAVG, gamma, 0–255) :

| Exposition (gain 20) | YAVG | | Gain (exposition −7) | YAVG |
|---|---|---|---|---|
| −10 | 18 | | 0 | 7 |
| −9 | 23 | | 20 | 37 |
| −8 | 30 | | 40 | 92 |
| −7 | 35 | | 60 | 146 |
| −6 | 42 | | 80 | 207 |
| −5 | 43 (saturé) | | 100 | 226 |

L'exposition n'a que six crans exploitables, espacés d'environ 20 %. Le gain
couvre toute la plage utile en cent crans. Corriger au gain est donc environ
trois fois plus fin à chaque pas, avec seize fois plus de paliers.

**2. Le flux vidéo est exclusif.** Deux captures simultanées échouent, quelle
que soit la résolution, et même entre une application Media Foundation et une
application DirectShow alors que le Frame Server de Windows est actif. Il est
donc impossible de mesurer la lumière pendant une visioconférence.

**3. En mode automatique, la valeur d'exposition relue est une valeur morte.**
Sur quatorze secondes de capture, elle ne bouge pas d'un cran, alors que la
luminance de l'image montre que la boucle du firmware travaille. On ne peut donc
pas non plus s'en servir de posemètre.

**4. Les propriétés restent pilotables pendant qu'une autre application filme.**
C'est ce qui permet de figer les réglages sans jamais gêner personne.

De ces contraintes découle le fonctionnement :

- L'exposition **et** le gain restent en manuel en permanence. Le firmware ne
  provoque donc plus aucun saut, jamais.
- La correction se fait au gain. L'exposition ne bouge que lorsque le gain
  arrive en butée, et le gain est alors recentré pour que la transition reste
  continue.
- La mesure n'a lieu que lorsque la caméra est libre, ou depuis les images de
  l'aperçu quand il est ouvert.
- **Quand une autre application filme, tout est gelé.** C'est justement le
  moment où l'on veut que rien ne bouge.
- **Aucune mesure dans les 20 secondes qui suivent la libération de la caméra.**
  Quitter une visioconférence et en ouvrir une autre dans la foulée est courant ;
  mesurer à cet instant refuserait la caméra à l'application suivante.
- La vitesse s'adapte à qui regarde : rapide quand la caméra est libre puisque
  personne ne voit la correction, un cran de gain par seconde au maximum quand
  l'aperçu est ouvert, nulle pendant une visioconférence.

Pour savoir si la caméra est libre, l'application lit le registre que Windows
tient pour son indicateur de confidentialité
(`CapabilityAccessManager\ConsentStore\webcam`) plutôt que d'essayer d'ouvrir le
périphérique, ce qui reviendrait à le bloquer.

Réglages, dans le profil : `TargetLuma` (luminosité visée, 0–255),
`Deadband` (zone morte en dessous de laquelle on ne bouge pas),
`GainMin` / `GainMax`, `ExposureMin` / `ExposureMax`,
`IdleIntervalSeconds`, `LiveStepMax`, `IdleStepMax`, `MeterWhenIdle`.

**Le compromis à connaître.** Une mesure occupe la caméra environ trois
secondes. Si une application la demande exactement à cet instant, elle se la
voit refuser — et la plupart ne réessaient pas toutes seules. Avec un cycle par
défaut de cinq minutes, cela représente environ 1 % du temps. Décocher
« Mesurer aussi quand l'aperçu est fermé » supprime complètement ce risque :
la mesure n'a alors lieu que lorsque l'aperçu est ouvert ou sur demande, au prix
d'une exposition qui ne suit plus la lumière de la pièce toute seule.

Limite assumée : pendant une visioconférence longue, si la lumière de la pièce
change beaucoup, l'exposition ne suivra pas. C'est le prix de l'absence totale
de saut, et le matériel ne permet pas de faire autrement.

## La vraie cause des réglages perdus

Le watchdog traite le symptôme. La cause la plus fréquente est la **suspension
sélective USB** : Windows endort la caméra, qui repart aux valeurs d'usine au
réveil. Pour la désactiver (session administrateur nécessaire) :

```powershell
.\tools\fix-usb-suspend.ps1           # affiche l'état
.\tools\fix-usb-suspend.ps1 -Apply    # désactive la mise en veille
.\tools\fix-usb-suspend.ps1 -Revert   # remet l'état d'origine
```

Le script agit sur deux leviers : le paramètre de suspension sélective du plan
d'alimentation actif, et l'autorisation d'extinction du concentrateur USB qui
porte la caméra.

## Notes techniques

- **Ne jamais garder le filtre DirectShow lié.** C'est la règle la plus
  importante du projet. Tant que le filtre est lié, aucune application Media
  Foundation — navigateur, donc Google Meet et Teams web, application Caméra de
  Windows — ne peut ouvrir la caméra : elle affiche un écran noir. Deux clients
  DirectShow, eux, cohabitent sans problème, ce qui rend le piège facile à
  manquer si l'on ne teste qu'avec ffmpeg. L'application ouvre donc à la demande
  et relâche aussitôt. Un cycle complet « lier, lire, écrire, libérer » coûte
  21 ms, et la connexion n'est maintenue que 700 ms après une action sur un
  curseur, pour que le réglage reste fluide sous la souris.
- **Un accès bref pendant qu'une autre application filme est inoffensif.**
  Mesuré : lecture et écriture des propriétés pendant que l'application Caméra
  diffuse, sans que l'image bronche. C'est ce qui permet au watchdog de
  restaurer les réglages qu'une application vient d'écraser en s'ouvrant.
- **ffmpeg peut être un lanceur.** Le `ffmpeg` du `PATH` est parfois un shim —
  celui de Chocolatey, par exemple — qui démarre le vrai binaire dans un
  processus enfant. Arrêter le processus lancé ne tue alors que le lanceur, et
  l'enfant continue de tenir la caméra indéfiniment. Chaque capture est donc
  placée dans un *job object* configuré en `KILL_ON_JOB_CLOSE`, ce qui emporte
  tout l'arbre à l'arrêt et garantit qu'aucun processus de capture ne survit à
  l'application, même si elle est tuée ou plante.
- **Les mesures se donnent une durée limite.** `-t 4` fait sortir ffmpeg de
  lui-même, ce qui rend la caméra proprement plutôt que par un arrêt forcé.
- **Aperçu sans graphe de rendu.** Plutôt que de monter un graphe DirectShow avec
  `IVideoWindow` — beaucoup d'interop fragile sur des interfaces duales —
  l'aperçu demande à ffmpeg de recopier telles quelles les trames MJPEG de la
  caméra (`-c:v copy`, donc aucun réencodage) et découpe le flux sur les
  marqueurs JPEG. ffmpeg doit être dans le `PATH`, sinon le chemin est
  configurable via `FfmpegPath` dans `config.json`.
- **Comparaison en mode auto.** Quand un réglage est en automatique, sa valeur lue
  dérive en permanence ; le watchdog ne compare alors que le mode, jamais la
  valeur, sinon il réécrirait sans arrêt.
- **Arrêt de ffmpeg par « q », pas par `Kill`.** Un processus tué laisse Windows
  croire que la caméra est encore utilisée : l'entrée du `ConsentStore` garde
  `LastUsedTimeStop` à zéro. L'auto-exposition se croirait alors bloquée
  indéfiniment. On envoie donc `q` sur l'entrée standard et on ne tue qu'en
  dernier recours.
- **Ne pas se prendre pour une autre application.** Le ffmpeg de mesure est
  enregistré sous son propre nom dans le `ConsentStore` : sans exclusion
  explicite, l'application se verrait elle-même comme une concurrente et
  interromprait sa propre mesure une seconde après l'avoir lancée.
- **`WM_DEVICECHANGE` n'est pas fiable comme signal de débranchement.** Ouvrir la
  caméra le déclenche aussi. Avant de reconnecter, on vérifie donc par une
  lecture que la session est réellement morte, faute de quoi chaque mesure
  rechargerait le profil et annulerait l'auto-exposition en cours.

## Limites connues

- L'aperçu peut échouer avec `Could not run graph` si une autre application tient
  déjà la caméra dans un format incompatible. Le pilotage des réglages, lui,
  continue de fonctionner. Fermer l'aperçu suffit.
- Le zoom, le panoramique et l'inclinaison de la N60 sont numériques : ils
  recadrent dans le capteur et coûtent donc de la définition.
