"""
Generates symbolicpatterns-fr-peur.json: 200 symbolic patterns (PUT /symbolicpattern) for French words of fear, 50 on
each theme: war, economy, terrorism and artificial intelligence. Each entry: the symbol is the first word; the regexes
match that word, its inflections and up to five synonyms (case ignored, whole words, accents optional); the signal
!FEAR, no event; the title, tags and comment follow the meaning of the words; language FR. The ids are stable
(fr-fear-<symbol>).

Usage: python make_symbolicpatterns_fr_peur.py  (writes the JSON next to this script)
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import seedpatterns  # noqa: E402

# symbol | tags | comment | regex bodies (the head word first, then its synonyms), separated by ';'
# In the bodies, an accented letter also matches its plain form (é -> [ée]).
DATA = """
guerre|#FEAR #GUERRE|Un conflit armé entre États ou groupes.|guerre(s)?;guerre(s)? mondiale(s)?;troisième guerre mondiale;état de guerre;déclaration de guerre
conflit|#FEAR #GUERRE|Un affrontement armé.|conflit(s)?;conflit(s)? armé(s)?;affrontement(s)?;hostilités;zone(s)? de conflit
invasion|#FEAR #GUERRE|L'entrée de forces armées dans un pays.|invasion(s)?;envah(ir|i|ie|is|it|issent|isseur|isseurs)|incursion(s)?;offensive(s)? terrestre(s)?
bombardement|#FEAR #GUERRE|Une attaque par des bombes.|bombardement(s)?;bombard(er|é|ée|és|ées|ent)|pilonnage(s)?;pilonn(er|é|ée)|tapis de bombes
missile|#FEAR #GUERRE|Un projectile autopropulsé.|missile(s)?;roquette(s)?;tir(s)? de missile(s)?;missile(s)? balistique(s)?;ogive(s)?
bombe|#FEAR #GUERRE|Un engin explosif.|bombe(s)?;obus;bombe(s)? à fragmentation;bombe(s)? à sous-munitions;mine(s)? antipersonnel
arme|#FEAR #GUERRE|Un instrument fait pour tuer ou blesser.|arme(s)?;armement(s)?;arsenal;armes de destruction massive;munition(s)?
armée|#FEAR #GUERRE|Les forces militaires d'un pays.|armée(s)?;militaire(s)?;forces armées;état-major;régiment(s)?
soldat|#FEAR #GUERRE|Une personne qui combat dans une armée.|soldat(s)?;combattant(e|s|es)?;militaire(s)? tué(s)?;fantassin(s)?;réserviste(s)?
mobilisation|#FEAR #GUERRE|L'appel des troupes à la guerre.|mobilisation(s)? générale(s)?;mobilisation(s)?;mobilis(er|é|és) (les|des) (troupes|réservistes)|appel sous les drapeaux
combat|#FEAR #GUERRE|Un affrontement armé.|combat(s)?;combat(tre|tent|tu|tus) (sur le front|l'ennemi)|ligne(s)? de front;front de l'est;en première ligne
raid|#FEAR #GUERRE|Une attaque rapide, souvent aérienne.|raid(s)?;raid(s)? aérien(s)?;frappe(s)? aérienne(s)?;frappe(s)? de missiles;attaque(s)? aérienne(s)?
drone|#FEAR #GUERRE|Un aéronef sans pilote, souvent armé.|drone(s)?;drone(s)? kamikaze(s)?;drone(s)? armé(s)?;attaque(s)? de drones;essaim(s)? de drones
blindé|#FEAR #GUERRE|Un véhicule militaire protégé.|blindé(s)?;char(s)? d'assaut;char(s)? de combat;colonne(s)? de chars;tank(s)?
artillerie|#FEAR #GUERRE|Les canons et pièces de tir lourd.|artillerie(s)?;canon(s)?;tir(s)? d'artillerie;obusier(s)?;mortier(s)?
nucléaire|#FEAR #GUERRE|L'énergie et l'arme atomiques.|nucléaire(s)?;menace nucléaire;guerre nucléaire;arme(s)? nucléaire(s)?;hiver nucléaire
atomique|#FEAR #GUERRE|L'arme qui libère l'énergie de l'atome.|atomique(s)?;bombe(s)? atomique(s)?;bombe(s)? h;champignon atomique;retombées radioactives
belligérant|#FEAR #GUERRE|Une partie engagée dans une guerre.|belligérant(e|s|es)?;escalade militaire;escalade du conflit;montée des tensions;engrenage
offensive|#FEAR #GUERRE|Une attaque de grande ampleur.|offensive(s)?;contre-offensive(s)?;assaut(s)?;percée(s)? ennemie(s)?;attaque(s)? massive(s)?
encerclement|#FEAR #GUERRE|Le fait d'entourer une ville ou une armée.|encerclement(s)?;encercl(er|é|ée|és|ées)|ville(s)? assiégée(s)?;siège de la ville;assiég(er|é|ée)
envahisseur|#FEAR #GUERRE|Celui qui occupe un pays par la force.|envahisseur(s)?;occupant(s)?;occupation militaire;territoire(s)? occupé(s)?;force(s)? d'occupation
réfugié|#FEAR #GUERRE|Une personne qui fuit la guerre.|réfugié(e|s|es)?;exode(s)?;fuir la guerre;déplacé(e|s|es)? de guerre;camp(s)? de réfugiés
embargo|#FEAR #GUERRE #ECONOMIE|L'interdiction de commercer avec un pays.|embargo(s)?;sanction(s)? économique(s)?;mesures de rétorsion;gel des avoirs;boycott(s)?
ultimatum|#FEAR #GUERRE|Une dernière mise en demeure avant la force.|ultimatum(s)?;mise(s)? en demeure;menace(s)? d'intervention;dernier avertissement;bruits de bottes
réarmement|#FEAR #GUERRE|Le fait de s'armer à nouveau.|réarmement(s)?;réarm(er|é|ée)|course aux armements;militarisation(s)?;budget(s)? militaire(s)?
dissuasion|#FEAR #GUERRE|La menace qui empêche l'attaque.|dissuasion(s)?;dissuasi(f|ve|fs|ves);force de frappe;équilibre de la terreur;arsenal nucléaire
bataille|#FEAR #GUERRE|Un combat entre armées.|bataille(s)?;champ(s)? de bataille;combats acharnés;combats de rue;guérilla(s)?
tranchée|#FEAR #GUERRE|Un fossé où se protègent les soldats.|tranchée(s)?;guerre de position;no man's land;barbelé(s)?;casemate(s)?
espionnage|#FEAR #GUERRE|La recherche secrète d'informations.|espionnage(s)?;espion(ne|s|nes)?;agent(s)? secret(s)?;taupe(s)?;renseignement(s)? militaire(s)?
sabotage|#FEAR #GUERRE|La destruction volontaire et secrète.|sabotage(s)?;sabot(er|é|ée|eur|eurs)|acte(s)? de sabotage;infrastructure(s)? visée(s)?
blocus|#FEAR #GUERRE|L'encerclement qui coupe les approvisionnements.|blocus;blocus maritime;couper les vivres;asphyxie économique;isolement total
déportation|#FEAR #GUERRE|Le transfert forcé de populations.|déportation(s)?;déport(er|é|ée|és|ées)|transfert(s)? forcé(s)?;camp(s)? de concentration;nettoyage ethnique
exaction|#FEAR #GUERRE|Une violence commise contre des civils.|exaction(s)?;crime(s)? de guerre;crime(s)? contre l'humanité;atrocité(s)?;civils tués
pillage|#FEAR #GUERRE|Le vol par la violence.|pillage(s)?;pill(er|é|ée|ard|ards)|razzia(s)?;mise à sac;saccage(s)?
couvre-feu|#FEAR #GUERRE|L'interdiction de sortir la nuit.|couvre-feu(x)?;loi martiale;état de siège;état d'urgence;interdiction de circuler
sirène|#FEAR #GUERRE|L'alarme qui annonce un danger.|sirène(s)? d'alerte;alerte(s)? aérienne(s)?;alerte(s)? générale(s)?;sirène(s)?;abri(s)? anti-aérien(s)?
bunker|#FEAR #GUERRE|Un abri fortifié.|bunker(s)?;abri(s)? antiatomique(s)?;abri(s)? souterrain(s)?;blockhaus;se terrer
troupes|#FEAR #GUERRE|Les soldats déployés.|troupes;troupes au sol;déploiement(s)? militaire(s)?;contingent(s)?;renforts militaires
mercenaire|#FEAR #GUERRE|Un soldat payé par une puissance étrangère.|mercenaire(s)?;milice(s)?;milicien(ne|s|nes)?;paramilitaire(s)?;seigneur(s)? de guerre
insurrection|#FEAR #GUERRE|Un soulèvement armé.|insurrection(s)?;insurgé(e|s|es)?;guerre civile;soulèvement(s)?;rébellion(s)?
putsch|#FEAR #GUERRE|Une prise du pouvoir par la force.|putsch(s)?;coup d'état;junte(s)?;putschiste(s)?;renversement(s)? du pouvoir
dictature|#FEAR #GUERRE|Un régime autoritaire.|dictature(s)?;dictateur(s)?;régime autoritaire;totalitaire(s)?;tyrannie(s)?
tyran|#FEAR #GUERRE|Un dirigeant cruel et absolu.|tyran(s)?;despote(s)?;autocrate(s)?;oppresseur(s)?;bourreau(x)?
représailles|#FEAR #GUERRE|Une riposte violente.|représailles;riposte(s)?;rétorsion(s)?;vengeance(s)?;contre-attaque(s)?
annexion|#FEAR #GUERRE|L'appropriation d'un territoire par la force.|annexion(s)?;annex(er|é|ée)|conquête(s)?;expansionnisme(s)?;velléités territoriales
fusillade|#FEAR #GUERRE #TERRORISME|Des tirs nourris d'armes à feu.|fusillade(s)?;tir(s)? nourri(s)?;échange(s)? de tirs;rafale(s)?;coups de feu
évacuation|#FEAR #GUERRE|Le fait de faire partir une population en danger.|évacuation(s)?;évacu(er|é|ée|és|ées)|corridor(s)? humanitaire(s)?;fuite(s)? des civils;exfiltration(s)?
conscription|#FEAR #GUERRE|L'enrôlement obligatoire dans l'armée.|conscription(s)?;conscrit(e|s|es)?;service militaire obligatoire;enrôlement(s)? forcé(s)?;appelé(s)? sous les drapeaux
cyberguerre|#FEAR #GUERRE #IA|La guerre menée par attaques informatiques.|cyberguerre(s)?;cyber-guerre(s)?;guerre hybride;guerre de l'information;cybermenace(s)?
crise|#FEAR #ECONOMIE|Une période de grave difficulté.|crise(s)?;crise(s)? économique(s)?;crise(s)? financière(s)?;crise(s)? de la dette;crise(s)? énergétique(s)?
récession|#FEAR #ECONOMIE|Le recul de l'activité économique.|récession(s)?;récessionniste(s)?;croissance négative;ralentissement(s)? économique(s)?;dépression économique
inflation|#FEAR #ECONOMIE|La hausse générale des prix.|inflation(s)?;inflationniste(s)?;hausse des prix;flambée des prix;vie chère
chômage|#FEAR #ECONOMIE|Le manque d'emploi.|chômage(s)?;sans emploi;demandeur(s)? d'emploi;perte(s)? d'emploi;taux de chômage
licenciement|#FEAR #ECONOMIE|La rupture du contrat de travail par l'employeur.|licenciement(s)?;licenci(er|é|ée|és|ées)|plan(s)? social(aux)?;plan(s)? de licenciement;suppression(s)? de postes
krach|#FEAR #ECONOMIE|L'effondrement brutal de la Bourse.|krach(s)?;krach boursier;chute des marchés;effondrement des marchés;lundi noir
banqueroute|#FEAR #ECONOMIE|La faillite.|banqueroute(s)?;faillite(s)?;cessation de paiement(s)?;dépôt de bilan;mise en faillite
dette|#FEAR #ECONOMIE|L'argent que l'on doit.|dette(s)?;dette publique;créance(s)?;créancier(s)?;remboursement(s)? impossible(s)?
endettement|#FEAR #ECONOMIE|Le fait d'être endetté.|endettement(s)?;endett(é|ée|és|ées|er)|criblé(e|s|es)? de dettes;dans le rouge;fin(s)? de mois difficile(s)?
déficit|#FEAR #ECONOMIE|Le manque d'argent dans un budget.|déficit(s)?;déficitaire(s)?;trou budgétaire;dérapage(s)? budgétaire(s)?;dans le rouge
austérité|#FEAR #ECONOMIE|La politique de restriction des dépenses.|austérité(s)?;cure d'austérité;coupe(s)? budgétaire(s)?;rigueur budgétaire;tour de vis
pénurie|#FEAR #ECONOMIE|Le manque d'un produit nécessaire.|pénurie(s)?;rupture(s)? de stock;étals vides;rayons vides;manque de carburant
précarité|#FEAR #ECONOMIE|L'instabilité des ressources et de l'emploi.|précarité(s)?;précaire(s)?;emploi(s)? précaire(s)?;fin de droits;travailleur(s)? pauvre(s)?
dévaluation|#FEAR #ECONOMIE|La baisse de la valeur d'une monnaie.|dévaluation(s)?;dévalu(er|é|ée)|chute de la monnaie;monnaie qui s'effondre;dépréciation monétaire
stagflation|#FEAR #ECONOMIE|La stagnation avec inflation.|stagflation(s)?;stagnation économique;croissance en berne;économie à l'arrêt
spéculation|#FEAR #ECONOMIE|Les opérations risquées sur les marchés.|spéculation(s)?;spéculati(f|fs|ve|ves);spécul(er|ateur|ateurs)|fonds vautour(s)?;casino financier
surchauffe|#FEAR #ECONOMIE|Une hausse excessive qui finit par éclater.|surchauffe(s)?;bulle(s)? spéculative(s)?;bulle(s)? immobilière(s)?;bulle(s)? financière(s)?;éclatement de la bulle;bulle internet
dégringolade|#FEAR #ECONOMIE|Une chute rapide.|dégringolade(s)?;dégringol(er|e|ent|é)|plongeon de la bourse;chute libre;dévissage(s)?
délocalisation|#FEAR #ECONOMIE|Le transfert d'activités à l'étranger.|délocalisation(s)?;délocalis(er|é|ée|és)|fermeture(s)? d'usine(s)?;usine(s)? fermée(s)?;emplois délocalisés
restructuration|#FEAR #ECONOMIE|La réorganisation qui supprime des emplois.|restructuration(s)?;restructur(er|é|ée)|réduction(s)? d'effectifs;dégraissage(s)?;compression(s)? de personnel
déflation|#FEAR #ECONOMIE|La baisse générale et durable des prix.|déflation(s)?;déflationniste(s)?;spirale déflationniste;baisse généralisée des prix
flambée|#FEAR #ECONOMIE|Une hausse brutale.|flambée(s)?;envolée des prix;prix qui explosent;prix de l'énergie;facture(s)? qui explose(nt)?
surendettement|#FEAR #ECONOMIE|Le fait de ne plus pouvoir payer ses dettes.|surendettement(s)?;surendetté(e|s|es)?;dossier de surendettement;commission de surendettement
impayé|#FEAR #ECONOMIE|Une somme due non payée.|impayé(e|s|es)?;loyer(s)? impayé(s)?;facture(s)? impayée(s)?;retard(s)? de paiement;relance(s)? de paiement
expulsion|#FEAR #ECONOMIE|Le fait d'être chassé de son logement.|expulsion(s)?;expuls(er|é|ée|és|ées)|mis(e|es)? à la porte;perdre (son|mon|ton) logement;à la rue
saisie|#FEAR #ECONOMIE|La confiscation de biens pour dettes.|saisie(s)? (sur salaire|immobilière|des biens)|saisie(s)?;confiscation(s)?;biens saisis;vente aux enchères forcée
huissier|#FEAR #ECONOMIE|L'officier qui recouvre les dettes.|huissier(s)?;commissaire(s)? de justice;recouvrement(s)?;commandement de payer;mise en demeure
insolvabilité|#FEAR #ECONOMIE|L'incapacité de payer ses dettes.|insolvabilité(s)?;insolvable(s)?;défaut de paiement;cessation de paiements;ne plus pouvoir payer
liquidation|#FEAR #ECONOMIE|La fin d'une entreprise et la vente de ses biens.|liquidation(s)? judiciaire(s)?;liquidation(s)?;entreprise(s)? liquidée(s)?;redressement judiciaire;procédure collective
chômeur|#FEAR #ECONOMIE|Une personne sans emploi.|chômeu(r|rs|se|ses);inscrit(e|s|es)? à pôle emploi;inscrit(e|s|es)? à france travail;sans travail;au chômage
rationnement|#FEAR #ECONOMIE|La limitation de ce que chacun peut acheter.|rationnement(s)?;rationn(er|é|ée)|ticket(s)? de rationnement;quota(s)? par personne;tickets de restriction
volatilité|#FEAR #ECONOMIE|Les variations brutales des marchés.|volatilité(s)?;volatil(e|es)?;marchés agités;séance noire;yo-yo des marchés
incertitude|#FEAR #ECONOMIE|Le fait de ne pas savoir ce qui va arriver.|incertitude(s)?;incertain(e|s|es)?;avenir sombre;climat d'incertitude;flou total
instabilité|#FEAR #ECONOMIE|Le manque de stabilité.|instabilité(s)?;instable(s)?;turbulence(s)?;tourmente(s)?;fragilité(s)? financière(s)?
défaillance|#FEAR #ECONOMIE|L'incapacité d'une entreprise à faire face.|défaillance(s)? d'entreprise(s)?;défaillance(s)?;défaut souverain;faillite(s)? en cascade;effet domino
délestage|#FEAR #ECONOMIE|Les coupures de courant organisées.|délestage(s)?;coupure(s)? de courant;panne(s)? générale(s)?;black-out;pénurie(s)? d'électricité
protectionnisme|#FEAR #ECONOMIE|La fermeture des frontières au commerce.|protectionnisme(s)?;protectionniste(s)?;guerre commerciale;droits de douane;guerre des tarifs
subprimes|#FEAR #ECONOMIE|Les crédits à risque à l'origine de la crise de 2008.|subprime(s)?;crise de 2008;crédit(s)? toxique(s)?;prêt(s)? toxique(s)?;actif(s)? toxique(s)?
désindustrialisation|#FEAR #ECONOMIE|La disparition des industries d'un pays.|désindustrialisation(s)?;friche(s)? industrielle(s)?;usines qui ferment;désert(s)? industriel(s)?
déclassement|#FEAR #ECONOMIE|La perte de son rang social.|déclassement(s)?;déclass(é|ée|és|ées)|descenseur social;classe(s)? moyenne(s)? appauvrie(s)?;perte de statut
dépréciation|#FEAR #ECONOMIE|La perte de valeur.|dépréciation(s)?;dépréci(er|é|ée)|moins-value(s)?;perte(s)? de valeur;actifs dépréciés
marasme|#FEAR #ECONOMIE|Une stagnation profonde.|marasme(s)?;morosité économique;économie en panne;croissance atone;climat morose
stagnation|#FEAR #ECONOMIE|L'absence de progression.|stagnation(s)?;stagn(er|e|ent|ant|ante)|piétinement(s)?;au point mort;croissance zéro
effritement|#FEAR #ECONOMIE|Une dégradation lente.|effritement(s)?;effrit(er|e|é|ée)|érosion(s)? du pouvoir d'achat;rogné(e|s|es)?;grignoté(e|s|es)?
paupérisation|#FEAR #ECONOMIE|L'appauvrissement d'une population.|paupérisation(s)?;appauvrissement(s)?;appauvr(ir|i|ie|is)|pouvoir d'achat en berne;fin du mois
inégalité|#FEAR #ECONOMIE|Les écarts injustes de richesse.|inégalité(s)?;inégalitaire(s)?;fracture sociale;écart(s)? de richesse;injustice(s)? sociale(s)?
terrorisme|#FEAR #TERRORISME|L'usage de la terreur à des fins politiques.|terrorisme(s)?;menace terroriste;acte(s)? terroriste(s)?;réseau(x)? terroriste(s)?;cellule(s)? terroriste(s)?
terroriste|#FEAR #TERRORISME|Une personne qui commet des attentats.|terroriste(s)?;poseur(s)? de bombe(s)?;assaillant(s)?;auteur(s)? de l'attentat;commando(s)? armé(s)?
attentat|#FEAR #TERRORISME|Une attaque criminelle contre des personnes.|attentat(s)?;attentat(s)? meurtrier(s)?;attaque(s)? terroriste(s)?;acte(s)? de terreur;tuerie(s)? de masse
kamikaze|#FEAR #TERRORISME|Un assaillant qui se tue en attaquant.|kamikaze(s)?;attentat(s)?-suicide;ceinture(s)? d'explosifs;ceinture(s)? explosive(s)?;mission suicide
radicalisation|#FEAR #TERRORISME|Le passage à des idées extrêmes et violentes.|radicalisation(s)?;radicalis(é|ée|és|ées|er)|basculement dans la violence;dérive(s)? sectaire(s)?
extrémisme|#FEAR #TERRORISME|Les idées poussées à l'extrême et à la violence.|extrémisme(s)?;extrémiste(s)?;ultra-violent(e|s|es)?;groupuscule(s)?;ultradroite;ultragauche
fanatisme|#FEAR #TERRORISME|Un zèle aveugle et violent.|fanatisme(s)?;fanatique(s)?;fanatis(é|ée|és|er)|intégrisme(s)?;sectarisme(s)?
endoctrinement|#FEAR #TERRORISME|Le fait d'imposer une doctrine par la manipulation.|endoctrinement(s)?;endoctrin(er|é|ée|és)|embrigadement(s)?;embrigad(er|é|ée)|lavage de cerveau
propagande|#FEAR #TERRORISME|La diffusion d'idées pour manipuler.|propagande(s)?;propagandiste(s)?;vidéo(s)? de revendication;apologie du terrorisme;recrutement en ligne
explosif|#FEAR #TERRORISME|Une substance ou un engin qui explose.|explosif(s)?;engin(s)? explosif(s)?;bombe(s)? artisanale(s)?;tatp;détonateur(s)?
otage|#FEAR #TERRORISME|Une personne retenue pour faire pression.|otage(s)?;prise(s)? d'otage(s)?;preneur(s)? d'otage(s)?;séquestration(s)?;retenu(e|s|es)? en otage
rançon|#FEAR #TERRORISME|La somme exigée pour libérer quelqu'un.|rançon(s)?;demande(s)? de rançon;libération contre rançon;extorsion(s)?;chantage(s)?
assaillant|#FEAR #TERRORISME|Celui qui attaque.|assaillant(e|s|es)?;agresseur(s)? armé(s)?;forcené(e|s|es)?;individu(s)? armé(s)?;tireur(s)?
tuerie|#FEAR #TERRORISME|Un meurtre de plusieurs personnes.|tuerie(s)?;fusillade(s)? de masse;massacre(s)?;bain de sang;carnage(s)?
vigipirate|#FEAR #TERRORISME|Le plan d'alerte contre le terrorisme.|vigipirate;urgence attentat;alerte attentat;niveau d'alerte maximal;sentinelle
suspect|#FEAR #TERRORISME|Un objet ou une personne qui inquiète.|colis suspect(s)?;bagage(s)? abandonné(s)?;objet(s)? suspect(s)?;suspect(e|s|es)?;individu suspect
piégé|#FEAR #TERRORISME|Un objet qui contient une bombe cachée.|piégé(e|s|es)?;voiture(s)? piégée(s)?;colis piégé(s)?;lettre(s)? piégée(s)?;engin piégé
déminage|#FEAR #TERRORISME|La neutralisation des explosifs.|déminage(s)?;démineur(s)?;périmètre de sécurité;levée de doute;robot démineur
attaque|#FEAR #TERRORISME|Une agression violente.|attaque(s)? au couteau;attaque(s)? à l'arme blanche;attaque(s)? armée(s)?;attaque(s)?;agression(s)? armée(s)?
égorgement|#FEAR #TERRORISME|Le fait de trancher la gorge.|égorgement(s)?;égorg(er|é|ée|és)|décapitation(s)?;décapit(er|é|ée)|poignard(er|é|ée)
barbarie|#FEAR #TERRORISME|Une cruauté extrême.|barbarie(s)?;barbare(s)?;atrocité(s)?;inhumain(e|s|es)?;sauvagerie(s)?
psychose|#FEAR #TERRORISME|Une peur collective.|psychose(s)?;peur collective;climat de peur;vent de panique;terreur généralisée
panique|#FEAR #TERRORISME|Une peur soudaine et incontrôlée.|panique(s)?;paniqu(er|é|ée|és|ées)|mouvement(s)? de foule;sauve-qui-peut;bousculade(s)?
confinement|#FEAR #TERRORISME|Le fait de rester enfermé à l'abri.|confinement(s)?;confin(er|é|ée|és|ées)|restez chez vous;mise(s)? à l'abri;bouclage(s)?
kalachnikov|#FEAR #TERRORISME|Un fusil d'assaut.|kalachnikov(s)?;ak-47;fusil(s)? d'assaut;arme(s)? de guerre;arme(s)? automatique(s)?
cyberterrorisme|#FEAR #TERRORISME #IA|Le terrorisme par attaques informatiques.|cyberterrorisme(s)?;cyberterroriste(s)?;attaque(s)? contre les infrastructures;sabotage informatique
bioterrorisme|#FEAR #TERRORISME|Le terrorisme par des agents biologiques.|bioterrorisme(s)?;arme(s)? biologique(s)?;anthrax;agent(s)? pathogène(s)?;enveloppe(s)? contaminée(s)?
sarin|#FEAR #TERRORISME|Un gaz de combat mortel.|sarin;gaz toxique(s)?;arme(s)? chimique(s)?;attaque(s)? chimique(s)?;gaz innervant(s)?
radiologique|#FEAR #TERRORISME|Une menace liée aux matières radioactives.|radiologique(s)?;bombe(s)? sale(s)?;matière(s)? radioactive(s)?;contamination radioactive;irradiation(s)?
complot|#FEAR #TERRORISME|Un projet secret contre quelqu'un.|complot(s)?;comploteur(s)?;conjuration(s)?;projet(s)? d'attentat;attentat déjoué
enrôlement|#FEAR #TERRORISME|L'enrôlement dans un groupe violent.|enrôlement(s)?;recrutement terroriste;recrue(s)? terroriste(s)?;filière(s)? terroriste(s)?;recrut(er|é|ée) par un groupe armé
fiché|#FEAR #TERRORISME|Une personne suivie par les services de sécurité.|fiché(e|s|es)? s;fiche s;fiché(e|s|es)?;sous surveillance;suivi(e|s|es)? par les services
revendication|#FEAR #TERRORISME|Le fait de déclarer être l'auteur d'un attentat.|revendication(s)?;revendiqu(er|é|ée)|a revendiqué l'attentat;communiqué de revendication
victimes|#FEAR #TERRORISME|Les personnes touchées par l'attaque.|victimes;bilan des victimes;bilan provisoire;blessés graves;morts et blessés
recueillement|#FEAR #TERRORISME|Le souvenir des victimes.|recueillement(s)?;hommage(s)? aux victimes;minute de silence;deuil national;marche blanche
sécuritaire|#FEAR #TERRORISME|La protection renforcée contre la menace.|sécuritaire(s)?;sécurité renforcée;mesures de sécurité;contrôles renforcés;haut niveau de sécurité
alerte|#FEAR #TERRORISME|L'avertissement d'un danger imminent.|alerte(s)?;alerte enlèvement;alerte maximale;danger imminent;menace imminente
effroi|#FEAR #TERRORISME|La peur qui fait fuir devant un danger.|effroi(s)?;fuir;s'enfuir;courir se cacher;prendre la fuite
traque|#FEAR #TERRORISME|La poursuite d'un suspect.|traque(s)?;traqu(er|é|ée|és)|chasse à l'homme;en cavale;avis de recherche
machette|#FEAR #TERRORISME|Une arme blanche.|machette(s)?;arme(s)? blanche(s)?;armé(e|s|es)? d'un couteau;couteau(x)? de boucher;hachoir(s)?
haine|#FEAR #TERRORISME|L'hostilité qui pousse à la violence.|discours de haine;appel(s)? au meurtre;haine(s)?;incitation à la haine;menace(s)? de mort
cible|#FEAR #TERRORISME|Ce qui est visé par une attaque.|cible(s)?;ciblé(e|s|es)?;visé(e|s|es)? par;lieu(x)? sensible(s)?;cible(s)? molle(s)?
IA|#FEAR #IA|L'intelligence artificielle et la peur qu'elle inspire.|ia;intelligence(s)? artificielle(s)?;i\\.a\\.;ia générative;ia forte
robot|#FEAR #IA|Une machine qui agit seule.|robot(s)?;robotique(s)?;androïde(s)?;humanoïde(s)?;automate(s)?
automatisation|#FEAR #IA|Le remplacement du travail humain par des machines.|automatisation(s)?;automatis(er|é|ée|és|ées)|remplacé(e|s|es)? par (une|des) machine(s)?;remplacé(e|s|es)? par l'ia;emplois menacés
algorithme|#FEAR #IA|Une suite d'instructions qui décide à notre place.|algorithme(s)?;algorithmique(s)?;décision(s)? automatisée(s)?;notation sociale;crédit social
surveillance|#FEAR #IA|Le contrôle permanent des personnes.|surveillance de masse;surveillance(s)?;big brother;société de surveillance;flicage(s)?
biométrie|#FEAR #IA|L'identification automatique des corps et des visages.|biométrie(s)?;biométrique(s)?;reconnaissance faciale;caméra(s)? intelligente(s)?;vidéosurveillance(s)?
deepfake|#FEAR #IA|Une fausse vidéo réaliste créée par l'IA.|deepfake(s)?;deep fake(s)?;hypertrucage(s)?;fausse(s)? vidéo(s)?;clonage de voix
désinformation|#FEAR #IA|La diffusion volontaire de fausses informations.|désinformation(s)?;fake news;fausse(s)? information(s)?;infox;intox
manipulation|#FEAR #IA|Le fait d'influencer à l'insu des gens.|manipulation(s)?;manipul(er|e|es|ent|é|ée|és|ées|ait|aient|ateur|atrice|ateurs)|influence(s)? cachée(s)?;ingérence(s)?;opération(s)? d'influence
singularité|#FEAR #IA|Le moment où l'IA dépasserait l'humain.|singularité(s)? technologique(s)?;singularité(s)?;dépasser l'humain;dépassé(e|s|es)? par l'ia
incontrôlable|#FEAR #IA|La perte de maîtrise des machines.|incontrôlable(s)?;perte de contrôle;hors de contrôle;échapper à tout contrôle;emballement
piratage|#FEAR #IA|L'accès illégal à un système informatique.|piratage(s)?;pirat(er|é|ée|és|ées)|hacker(s)?;hack(é|ée|és)|intrusion(s)? informatique(s)?
cyberattaque|#FEAR #IA|Une attaque contre des systèmes informatiques.|cyberattaque(s)?;cyber-attaque(s)?;attaque(s)? informatique(s)?;attaque(s)? par déni de service;ddos
rançongiciel|#FEAR #IA|Un logiciel qui bloque les données contre rançon.|rançongiciel(s)?;ransomware(s)?;données chiffrées;demande de rançon;logiciel(s)? malveillant(s)?
divulgation|#FEAR #IA|La divulgation de données privées.|divulgation(s)?;fuite(s)? de données;vol(s)? de données;données (volées|piratées|divulguées);brèche(s)? de sécurité;fichier(s)? volé(s)?
traçage|#FEAR #IA|Le suivi des faits et gestes de chacun.|traçage(s)?;tracking;géolocalis(é|ée|és|ation);pistage(s)?;suivi(e|s|es)? à la trace
robots-tueurs|#FEAR #IA #GUERRE|Les armes qui choisissent seules leurs cibles.|robots?-tueurs?;robot(s)? tueur(s)?;arme(s)? autonome(s)?;système(s)? d'armes létales autonomes
biais|#FEAR #IA|Un préjugé reproduit par une machine.|biais algorithmique(s)?;biais;discrimination(s)? algorithmique(s)?;algorithme(s)? discriminant(s)?;profilage(s)?
hallucination|#FEAR #IA|Une réponse inventée par une IA.|hallucination(s)?;hallucin(er|e|é|ée)|réponse(s)? inventée(s)?;erreur(s)? de l'ia;information(s)? fausse(s)?
transhumanisme|#FEAR #IA|L'idée d'augmenter l'humain par la technique.|transhumanisme(s)?;transhumaniste(s)?;humain augmenté;puce(s)? implantée(s)?;cyborg(s)?
obsolescence|#FEAR #IA|Le fait de devenir inutile, dépassé.|obsolescence(s)?;obsolète(s)?;métier(s)? qui disparai(ssen)?t;inutile face à la machine;devenu(e|s|es)? inutile(s)?
dépendance|#FEAR #IA|Le fait de ne plus pouvoir se passer d'une technologie.|dépendance(s)? numérique(s)?;dépendance(s)?;addiction(s)? aux écrans;accro(s)? aux écrans;cyberdépendance(s)?
censure|#FEAR #IA|Le contrôle de ce que l'on peut dire.|censure(s)?;censur(er|é|ée|és)|modération automatique;liberté d'expression menacée;shadowban
spyware|#FEAR #IA|Un logiciel qui écoute et observe en secret.|spyware(s)?;logiciel(s)? espion(s)?;pegasus;écoute(s)? illégale(s)?;micro(s)? caché(s)?
malware|#FEAR #IA|Un programme qui infecte les ordinateurs.|malware(s)?;virus informatique(s)?;cheval de troie;ver(s)? informatique(s)?;logiciel(s)? infecté(s)?
phishing|#FEAR #IA|Une arnaque pour voler des identifiants.|phishing;hameçonnage(s)?;arnaque(s)? en ligne;faux courriel(s)?;mail(s)? frauduleux
usurpation|#FEAR #IA|Le vol d'identité.|usurpation(s)? d'identité;usurpation(s)?;vol d'identité;identité volée;faux profil(s)?
déshumanisation|#FEAR #IA|La perte de l'humain dans les relations.|déshumanisation(s)?;déshumanis(é|ée|és|ant|ante)|sans contact humain;relation(s)? virtuelle(s)?;isolement numérique
uberisation|#FEAR #IA #ECONOMIE|La précarisation du travail par les plateformes.|uberisation(s)?;ubéris(é|ée|ation|er)|travailleur(s)? de plateforme;livreur(s)? précaire(s)?;micro-travail
terminator|#FEAR #IA|L'image de la machine qui se retourne contre l'humain.|terminator;skynet;révolte des machines;machines qui prennent le pouvoir;soulèvement des robots
extinction|#FEAR #IA|La disparition de l'humanité.|extinction(s)?;extinction de l'humanité;risque existentiel;menace existentielle;fin de l'humanité
dystopie|#FEAR #IA|Un futur où la technique opprime l'humain.|dystopie(s)?;dystopique(s)?;monde orwellien;meilleur des mondes;ia autonome
superintelligence|#FEAR #IA|L'idée d'une IA plus intelligente et consciente que l'humain.|superintelligence(s)?;conscience artificielle;machine consciente;ia consciente;ia sensible;éveil des machines
fichage|#FEAR #IA|La collecte des informations personnelles.|fichage(s)?;données personnelles;collecte de données;exploitation des données;données privées
vie privée|#FEAR #IA|Ce qui ne regarde que soi.|vie privée;atteinte à la vie privée;intimité violée;respect de la vie privée;plus aucune vie privée
prédictif|#FEAR #IA|Ce qui prétend prévoir nos actes.|prédicti(f|fs|ve|ves);police prédictive;justice prédictive;profilage prédictif;minority report
chatbot|#FEAR #IA|Un agent conversationnel automatique.|chatbot(s)?;agent(s)? conversationnel(s)?;faux humain;bot(s)?;compte(s)? automatisé(s)?
robotisation|#FEAR #IA #ECONOMIE|Le fait d'être remplacé par une IA ou un robot.|robotisation(s)?;remplace(r|nt)? (mon|ton|son|notre|votre|leur|nos|les) (métier|emploi|travail|job)(s)?;remplacé(e|s|es)? par un robot;destruction d'emplois;fin du travail;chômage technologique
arnaque|#FEAR #IA|Une escroquerie, souvent en ligne.|arnaque(s)?;arnaqu(er|é|ée|eur|eurs)|escroquerie(s)?;escroc(s)?;fraude(s)?
trucage|#FEAR #IA|Une image ou une voix falsifiée.|trucage(s)?;truquage(s)?;truqué(e|s|es)?;falsifi(é|ée|és|cation)|montage(s)? trompeur(s)?
emballement|#FEAR #IA|Une accélération que plus personne ne maîtrise.|emballement(s)?;course à l'ia;course aux armements de l'ia;fuite en avant;moratoire
cybercriminalité|#FEAR #IA|Les crimes commis sur internet.|cybercriminalité(s)?;cybercriminel(le|s|les)?;dark web;darknet;marché(s)? noir(s)? numérique(s)?
botnet|#FEAR #IA|Un réseau de machines piratées.|botnet(s)?;réseau(x)? de zombies;machine(s)? zombie(s)?;ordinateur(s)? zombie(s)?
réquisition|#FEAR #GUERRE|La mise au service de la guerre des biens et des personnes.|réquisition(s)?;réquisitionn(er|é|ée|és)|économie de guerre;effort de guerre;loi de mobilisation
hyperinflation|#FEAR #ECONOMIE|Une inflation hors de contrôle.|hyperinflation(s)?;inflation galopante;monnaie de singe;billet(s)? sans valeur;prix qui doublent
faillite|#FEAR #ECONOMIE|L'état d'une entreprise qui ne peut plus payer.|faillite(s)?;failli(e|s|es)?;mettre la clé sous la porte;fermer boutique;entreprise(s)? en difficulté
matraquage|#FEAR #ECONOMIE|Une pression fiscale très forte.|matraquage fiscal;matraquage(s)?;hausse(s)? d'impôts;ras-le-bol fiscal;pression fiscale
grève|#FEAR #ECONOMIE|L'arrêt collectif du travail.|grève(s)?;gréviste(s)?;grève générale;paralysie du pays;pays bloqué
addiction|#FEAR #IA|La dépendance aux écrans et aux applications.|addiction(s)?;addict(e|s|es)?;scrolling infini;temps d'écran;captation de l'attention
polarisation|#FEAR #IA|La division de la société par les algorithmes.|polarisation(s)?;bulle(s)? de filtre;chambre(s)? d'écho;algorithme(s)? de recommandation;société fracturée
cyberharcèlement|#FEAR #IA|Le harcèlement en ligne.|cyberharcèlement(s)?;harcèlement en ligne;raid numérique;doxxing;menace(s)? en ligne
clonage|#FEAR #IA|La copie numérique d'une personne.|clonage(s)?;voix clonée(s)?;clone(s)? numérique(s)?;avatar(s)? frauduleux;sosie(s)? numérique(s)?
monopole|#FEAR #IA #ECONOMIE|La domination de quelques géants de la technologie.|monopole(s)?;gafam;big tech;géants du numérique;concentration du pouvoir
panne|#FEAR #IA|L'arrêt brutal des systèmes informatiques.|panne(s)? informatique(s)?;panne(s)? géante(s)?;panne(s)?;bug(s)? géant(s)?;systèmes en panne
intrusion|#FEAR #IA|L'accès non autorisé à un système ou un compte.|intrusion(s)?;accès non autorisé;compte(s)? piraté(s)?;mot(s)? de passe volé(s)?;identifiants volés
explosion|#FEAR #TERRORISME|Une déflagration violente.|explosion(s)?;explos(er|é|ée)|déflagration(s)?;souffle de l'explosion;détonation(s)?
terreur|#FEAR #TERRORISME|Une peur extrême et collective.|terreur(s)?;terroris(é|ée|és|ées|er)|terrifi(é|ée|és|ées|ant|ante|er)|semer la terreur;climat de terreur;horrifi(é|ée|és|ant|ante)
menace|#FEAR #TERRORISME|L'annonce d'un mal à venir.|menace(s)?;menac(er|é|ée|ant|ante)|menace(s)? de mort;lettre(s)? de menace;intimidation(s)?
danger|#FEAR #TERRORISME|Ce qui menace la sécurité.|danger(s)?;dangereu(x|se|ses);péril(s)?;en danger;mise(s)? en danger
enlèvement|#FEAR #TERRORISME|Le rapt d'une personne.|enlèvement(s)?;kidnapping(s)?;kidnapp(er|é|ée|eur|eurs)|rapt(s)?;séquestré(e|s|es)?
cagoule|#FEAR #TERRORISME|Un visage masqué qui inquiète.|cagoule(s)?;cagoulé(e|s|es)?;homme(s)? masqué(s)?;individu(s)? masqué(s)?;visage dissimulé
émeute|#FEAR #TERRORISME|Un soulèvement violent dans la rue.|émeute(s)?;émeutier(s)?;violences urbaines;guérilla urbaine;affrontements avec la police
lynchage|#FEAR #TERRORISME|La violence d'une foule contre une personne.|lynchage(s)?;lynch(er|é|ée)|vindicte;foule en colère;passage à tabac
peur|#FEAR #EMOTION|L'émotion face à un danger.|peur(s)?;effray(é|ée|és|ées|er|ant|ante)|effroi;apeuré(e|s|es)?;épouvant(e|é|ée|able|ables)
angoisse|#FEAR #EMOTION|Une inquiétude profonde, une oppression.|angoisse(s)?;angoiss(é|ée|és|ées|ant|ante|er);anxiété(s)?;anxieu(x|se|ses);panique(s)?
"""

if __name__ == "__main__":
    seedpatterns.write(DATA, "fr-fear", "!FEAR", 202, pathlib.Path(__file__).with_name("symbolicpatterns-fr-peur.json"))
