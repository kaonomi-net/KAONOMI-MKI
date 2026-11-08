"""
Generates symbolicpatterns-fr-degout.json: 200 symbolic patterns (PUT /symbolicpattern) for French words of disgust,
physical (dirt, smells, the body, vermin, spoiled food, insalubrity) and moral (corruption, hypocrisy, cruelty,
contempt…). Each entry: the symbol is the first word; the regexes match that word, its inflections and up to five
synonyms (case ignored, whole words, accents optional); the signal !DISGUST, no event; the title, tags and comment
follow the meaning of the words; language FR. The ids are stable (fr-disgust-<symbol>).

Usage: python make_symbolicpatterns_fr_degout.py  (writes the JSON next to this script)
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import seedpatterns  # noqa: E402

# symbol | tags | comment | regex bodies (the head word first, then its synonyms), separated by ';'
# In the bodies, an accented letter also matches its plain form (é -> [ée]).
DATA = """
dégoût|#DISGUST|Une vive répugnance.|dégoût(s)?;dégoût(er|é|ée|és|ées|e|ent|ant|ante|ants|antes)|dégoûté(e|s|es)? de;ça me dégoûte;quel dégoût
écœurement|#DISGUST|Un dégoût qui soulève le cœur.|écœurement(s)?;écœur(er|é|ée|és|ées|e|ent|ant|ante|ants|antes)|ça m'écœure;à vomir;à gerber
répugnance|#DISGUST|Une forte aversion physique ou morale.|répugnance(s)?;répugn(er|e|ent|ait|ant|ante|ants|antes)|me répugne;répulsion(s)?;révulsion(s)?
nausée|#DISGUST #CORPS|L'envie de vomir.|nausée(s)?;nauséeu(x|se|ses);mal au cœur;envie de vomir;cœur au bord des lèvres
haut-le-cœur|#DISGUST #CORPS|Un spasme de dégoût.|haut(s)?-le-cœur;réflexe nauséeux;spasme(s)? de dégoût;soulève le cœur;retourne l'estomac
beurk|#DISGUST|L'interjection du dégoût.|beurk;berk;pouah;yuck;bêêh
dégueulasse|#DISGUST|Très sale ou répugnant (familier).|dégueulasse(s)?;dégueu;dégueulasserie(s)?;crade(s)?;cradingue(s)?
immonde|#DISGUST|D'une laideur ou d'une saleté repoussante.|immonde(s)?;immondice(s)?;innommable(s)?;atroce(s)?;effroyable(s)?
répugnant|#DISGUST|Qui provoque le dégoût.|répugnant(e|s|es)?;dégoûtant(e|s|es)?;écœurant(e|s|es)?;rebutant(e|s|es)?;rédhibitoire(s)?
repoussant|#DISGUST|Qui fait reculer de dégoût.|repoussant(e|s|es)?;hideu(x|se|ses);laid(e|s|es)? à faire peur;monstrueu(x|se|ses);difforme(s)?
ignoble|#DISGUST #MORAL|D'une bassesse qui écœure.|ignoble(s)?;ignominieu(x|se|ses);vil(e|s|es)?;méprisable(s)?;indigne(s)?
infâme|#DISGUST #MORAL|Qui inspire le dégoût moral.|infâme(s)?;infamie(s)?;scélérat(e|s|es)?;crapuleu(x|se|ses);odieu(x|se|ses)
abject|#DISGUST #MORAL|D'une bassesse révoltante.|abject(e|s|es)?;abjection(s)?;révoltant(e|s|es)?;inqualifiable(s)?;honteu(x|se|ses)
aversion|#DISGUST|Un rejet profond.|aversion(s)?;allergie à;phobie(s)?;horreur de;ne pas supporter
exécrable|#DISGUST|Extrêmement mauvais.|exécrable(s)?;exécr(er|é|ée|ation)|détestable(s)?;abominable(s)?;insupportable(s)?
vomitif|#DISGUST #CORPS|Qui donne envie de vomir.|vomiti(f|fs|ve|ves);gerbant(e|s|es)?;vomitoire(s)?;à vomir;à dégueuler
gerbe|#DISGUST #CORPS|L'envie de vomir (familier).|la gerbe;ger(ber|be|bé|bait)|dégueul(er|é|ait)|refouler du goulot;ça donne la gerbe
indigeste|#DISGUST #NOURRITURE|Qui écœure par l'excès.|trop sucré;trop gras;indigeste(s)?;lourd sur l'estomac;bourratif(s)?;écœurant à force
horreur|#DISGUST|Ce qui inspire une vive répulsion.|horreur(s)?;quelle horreur;horrible(s)?;affreu(x|se|ses);épouvantable(s)?;cauchemardesque(s)?
dégénéré|#DISGUST #MORAL|Qui a perdu toute qualité.|dégénéré(e|s|es)?;dégénérescence(s)?;taré(e|s|es)?;débile(s)? profond(e|s|es)?;tordu(e|s|es)?
vulgarité|#DISGUST #MORAL|Le manque de délicatesse.|vulgarité(s)?;vulgaire(s)?;grossièreté(s)?;grossi(er|ère|ers|ères);trivial(e|es|aux)?
obscénité|#DISGUST #MORAL|Ce qui blesse la pudeur.|obscénité(s)?;obscène(s)?;indécence(s)?;indécent(e|s|es)?;impudique(s)?
glauque|#DISGUST|Lugubre et malsain.|glauque(s)?;sordide(s)?;sinistre(s)?;lugubre(s)?;morbide(s)?
malsain|#DISGUST #MORAL|Qui est mauvais pour le corps ou l'esprit.|malsain(e|s|es)?;toxique(s)?;nocif(s)?;nocive(s)?;délétère(s)?
sale|#DISGUST #SALETE|Qui n'est pas propre.|sale(s)?;sali(r|e|s|es)?;souillé(e|s|es)?;crasseu(x|se|ses);pas propre
saleté|#DISGUST #SALETE|Ce qui rend sale.|saleté(s)?;malpropreté(s)?;manque d'hygiène;négligé(e|s|es)?;dégoûtant de saleté
crasse|#DISGUST #SALETE|Une saleté épaisse.|crasse(s)?;encrassé(e|s|es)?;encrassement(s)?;couche de crasse;noir de crasse
ordure|#DISGUST #SALETE|Ce qui est jeté ; une personne méprisable.|ordure(s)?;ordures ménagères;tas d'ordures;raclure(s)?;pourriture(s)? humaine(s)?
poubelle|#DISGUST #SALETE|Le récipient des déchets.|poubelle(s)?;poubelle(s)? qui déborde(nt)?;benne(s)?;vide-ordures;bac(s)? à ordures
déchet|#DISGUST #SALETE|Ce qui reste et que l'on jette.|déchet(s)?;détritus;rebut(s)?;résidu(s)?;débris
boue|#DISGUST #SALETE|La terre détrempée.|boue(s)?;boueu(x|se|ses);fange(s)?;bourbier(s)?;vase(s)?
souillure|#DISGUST #SALETE|Une tache qui salit.|souillure(s)?;souill(er|é|ée|és|ées)|maculé(e|s|es)?;tach(é|ée|és|ées)|éclaboussé(e|s|es)?
graisse|#DISGUST #SALETE|Une matière grasse qui colle.|graisse(s)?;graisseu(x|se|ses);huileu(x|se|ses);dégoulinant(e|s|es)? de graisse;cambouis
gluant|#DISGUST #SALETE|Qui colle comme de la glu.|gluant(e|s|es)?;visqueu(x|se|ses);poisseu(x|se|ses);collant(e|s|es)?;gélatineu(x|se|ses)
moisissure|#DISGUST #SALETE|Le champignon qui se forme sur ce qui pourrit.|moisissure(s)?;moisi(e|s|es|r)?;moisissant(e|s|es)?;champignon(s)? noir(s)?;salpêtre
pourri|#DISGUST #SALETE|En décomposition.|pourri(e|s|es)?;pourri(r|t|ssent|ssait|ssant|ssante)|pourriture(s)?;gâté(e|s|es)?;blet(te|s|tes)?
putréfaction|#DISGUST #SALETE|La décomposition des matières organiques.|putréfaction(s)?;putréfi(é|ée|és|ées|er)|putride(s)?;décomposition(s)?;en décomposition
fumier|#DISGUST #SALETE|Les excréments d'animaux mêlés de paille.|fumier(s)?;purin(s)?;lisier(s)?;bouse(s)?;crottin(s)?
égout|#DISGUST #SALETE|La canalisation des eaux sales.|égout(s)?;bouche(s)? d'égout;fosse(s)? septique(s)?;cloaque(s)?;caniveau(x)? puant(s)?
dégoulinant|#DISGUST #SALETE|Qui coule de façon répugnante.|dégoulinant(e|s|es)?;dégoulin(er|e|ent|ait)|suintant(e|s|es)?;suint(er|e|ent)|ruisselant(e|s|es)? de crasse
insalubre|#DISGUST #INSALUBRITE|Malsain, contraire à l'hygiène.|insalubre(s)?;insalubrité(s)?;logement(s)? indigne(s)?;inhabitable(s)?;délabré(e|s|es)?
taudis|#DISGUST #INSALUBRITE|Un logement misérable et sale.|taudis;gourbi(s)?;bouge(s)?;porcherie(s)?;écurie(s)? (immonde|crasseuse)(s)?
dépotoir|#DISGUST #INSALUBRITE|Le lieu où l'on dépose les ordures.|décharge(s)? sauvage(s)?;décharge(s)? publique(s)?;dépôt(s)? d'ordures;dépotoir(s)?;montagne(s)? de déchets
latrines|#DISGUST #INSALUBRITE|Des toilettes sommaires et sales.|latrine(s)?;toilettes bouchées;toilettes immondes;chiottes;wc bouchés
pollution|#DISGUST #INSALUBRITE|La contamination de l'environnement.|pollution(s)?;pollu(é|ée|és|ées|ant|ants|er)|marée(s)? noire(s)?;eaux usées;smog
contaminé|#DISGUST #INSALUBRITE|Souillé par un agent nocif.|contaminé(e|s|es)?;contamination(s)?;infecté(e|s|es)?;souillé(e|s|es)? par;impropre(s)? à la consommation
infesté|#DISGUST #INSALUBRITE|Envahi par des bêtes nuisibles.|infesté(e|s|es)?;infestation(s)?;grouillant(e|s|es)?;grouill(er|e|ent|ait)|envahi(e|s|es)? de (rats|cafards|vermine)
puanteur|#DISGUST #ODEUR|Une odeur très désagréable.|puanteur(s)?;pestilence(s)?;pestilentiel(le|s|les)?;infection(s)? (olfactive|totale);ça sent mauvais
puer|#DISGUST #ODEUR|Sentir très mauvais.|pu(er|e|ent|ait|aient|é)|puant(e|s|es)?;empest(er|e|ent|ait)|schlingu(er|e|ent)|refoul(er|e) du goulot
relent|#DISGUST #ODEUR|Une mauvaise odeur qui persiste.|relent(s)?;remugle(s)?;odeur(s)? de renfermé;odeur(s)? nauséabonde(s)?;mauvaise(s)? odeur(s)?
fétide|#DISGUST #ODEUR|Qui sent très mauvais.|fétide(s)?;nauséabond(e|s|es)?;méphitique(s)?;âcre(s)?;suffocant(e|s|es)?
infect|#DISGUST #ODEUR|Qui sent ou a un goût répugnant.|infect(e|s|es)?;innommable(s)?;imbuvable(s)?;immangeable(s)?;ça pue
transpiration|#DISGUST #ODEUR #CORPS|La sueur et son odeur.|transpiration(s)?;sueur(s)?;odeur(s)? de transpiration;aisselle(s)? qui sent(ent)?;dessous de bras
halitose|#DISGUST #ODEUR #CORPS|L'odeur du souffle.|halitose(s)?;mauvaise haleine;haleine fétide;haleine de chacal;haleine chargée;sent de la bouche
pet|#DISGUST #ODEUR #CORPS|Un gaz intestinal.|pet(s)?;pét(er|é|ait)|flatulence(s)?;vent(s)? (lâché|bruyant)(s)?;prout(s)?
rot|#DISGUST #CORPS|Un renvoi d'air bruyant.|rot(s)?;rot(er|é|ait)|éructation(s)?;renvoi(s)?;roter à table
vomi|#DISGUST #CORPS|Ce qui a été vomi.|vomi(s)?;vomissure(s)?;dégueulis;flaque(s)? de vomi;régurgitation(s)?
vomir|#DISGUST #CORPS|Rejeter par la bouche le contenu de l'estomac.|vomi(r|t|ssent|ssait|ssement|ssements)|rend(re|u) (son|tout son) repas;régurgit(er|é|ée)|dégobill(er|é)|rendre tripes et boyaux
crachat|#DISGUST #CORPS|De la salive rejetée.|crachat(s)?;crach(er|e|ent|é|ait|oter)|glaviot(s)?;mollard(s)?;postillon(s)?
morve|#DISGUST #CORPS|Le mucus du nez.|morve(s)?;morveu(x|se|ses);mucus;mucosité(s)?;nez qui coule
pus|#DISGUST #CORPS|Le liquide d'une plaie infectée.|pus;purulent(e|s|es)?;suppur(er|ant|ante|ation)|abcès;plaie(s)? infectée(s)?
bave|#DISGUST #CORPS|La salive qui coule.|bave(s)?;bav(er|e|ent|eu|euse|eux)|salive(s)? qui coule;écume(s)? aux lèvres;filet(s)? de bave
excrément|#DISGUST #CORPS|Les matières fécales.|excrément(s)?;matière(s)? fécale(s)?;fèces;déjection(s)?;étron(s)?
merde|#DISGUST #CORPS|Les excréments (vulgaire).|merde(s)?;merdique(s)?;emmerd(ant|ante|ement)|crotte(s)?;caca(s)?
urine|#DISGUST #CORPS|Le liquide évacué par la vessie.|urine(s)?;urin(er|é|ait)|pisse(s)?;odeur(s)? d'urine;pipi(s)? partout
diarrhée|#DISGUST #CORPS|Des selles liquides et fréquentes.|diarrhée(s)?;chiasse(s)?;courante(s)?;gastro(-entérite)?;turista
furoncle|#DISGUST #CORPS|Une inflammation purulente de la peau.|furoncle(s)?;anthrax cutané;kyste(s)?;bubon(s)?;pustule(s)?
verrue|#DISGUST #CORPS|Une petite excroissance de la peau.|verrue(s)?;excroissance(s)?;verruqueu(x|se|ses);chancre(s)?;lésion(s)? cutanée(s)?
acné|#DISGUST #CORPS|Les boutons sur la peau.|acné;acnéique(s)?;point(s)? noir(s)?;bouton(s)? d'acné;peau grasse
desquamation|#DISGUST #CORPS|La couche sèche sur une plaie.|croûte(s)? (de sang|purulente)(s)?;croûteu(x|se|ses);desquamation(s)?;peau morte;squame(s)?
pellicules|#DISGUST #CORPS|Les petites peaux mortes du cuir chevelu.|pellicule(s)? (sur|dans) (les cheveux|le col)|pellicules;cheveux gras;cheveu(x)? dans la soupe;tignasse sale
cérumen|#DISGUST #CORPS|La cire des oreilles.|cérumen;cire d'oreille;bouchon(s)? de cérumen;crotte(s)? de nez;ongles noirs
gangrène|#DISGUST #CORPS|L'invasion de microbes.|infection(s)? (purulente|nosocomiale|cutanée)(s)?;gangrène(s)?;gangren(é|ée|eux)|nécrose(s)?;septicémie(s)?
rat|#DISGUST #VERMINE|Un rongeur porteur de maladies.|rat(s)?;surmulot(s)?;rongeur(s)?;souris crevée(s)?;dératisation(s)?
cafard|#DISGUST #VERMINE|Un insecte qui vit dans la saleté.|cafard(s)?;blatte(s)?;cancrelat(s)?;cafards dans la cuisine;désinsectisation(s)?
asticot|#DISGUST #VERMINE|La larve de la mouche.|asticot(s)?;larve(s)?;ver(s)? de terre;ver(s)? blanc(s)?;grouillant(e|s|es)? de vers
vermine|#DISGUST #VERMINE|Les bêtes nuisibles et sales.|vermine(s)?;nuisible(s)?;parasite(s)?;bestiole(s)?;sale(s)? bête(s)?
punaise|#DISGUST #VERMINE|Un insecte suceur de sang.|punaise(s)?;punaise(s)? de lit;piqûre(s)? de punaise(s)?;matelas infesté;lit infesté
pou|#DISGUST #VERMINE|Un parasite des cheveux.|poux;pou;lente(s)?;pouilleu(x|se|ses);shampoing anti-poux
limace|#DISGUST #VERMINE|Un mollusque gluant.|limace(s)?;bave de limace;escargot(s)? écrasé(s)?;gastéropode(s)?;trace(s)? gluante(s)?
sangsue|#DISGUST #VERMINE|Un ver qui suce le sang.|sangsue(s)?;tique(s)?;parasite(s)? suceur(s)?;suceur(s)? de sang;vampire(s)?
mille-pattes|#DISGUST #VERMINE|Un animal aux nombreuses pattes.|mille-pattes;scolopendre(s)?;cloporte(s)?;perce-oreille(s)?;insecte(s)? rampant(s)?
charogne|#DISGUST #VERMINE|Un cadavre d'animal en décomposition.|charogne(s)?;cadavre(s)? d'animal;bête(s)? crevée(s)?;crevé(e|s|es)? au bord de la route;charognard(s)?
mouche|#DISGUST #VERMINE|Un insecte attiré par les ordures.|mouche(s)? (à merde|verte|bleue)(s)?;essaim(s)? de mouches;nuée(s)? de mouches;mouche(s)?;grouillement de mouches
périmé|#DISGUST #NOURRITURE|Dont la date de consommation est passée.|périmé(e|s|es)?;date dépassée;dlc dépassée;plus consommable(s)?;hors date
avarié|#DISGUST #NOURRITURE|Gâté, impropre à la consommation.|avarié(e|s|es)?;viande avariée;tourné(e|s|es)? (le lait|la crème);aliment(s)? gâté(s)?;denrée(s)? avariée(s)?
rance|#DISGUST #NOURRITURE|Qui a pris une odeur et un goût âcres.|rance(s)?;rancir;ranci(e|s|es)?;aigre(s)?;tourné(e|s|es)?
immangeable|#DISGUST #NOURRITURE|Qu'on ne peut pas manger.|immangeable(s)?;inmangeable(s)?;infect(e|s|es)? au goût;dégueu à manger;on ne peut pas avaler ça
frelaté|#DISGUST #NOURRITURE|Altéré, falsifié.|frelaté(e|s|es)?;falsifié(e|s|es)?;trafiqué(e|s|es)?;alcool frelaté;produit(s)? frelaté(s)?
malbouffe|#DISGUST #NOURRITURE|La mauvaise alimentation.|malbouffe;junk food;nourriture industrielle;plat(s)? ultra-transformé(s)?;bouffe dégueulasse
salmonelle|#DISGUST #NOURRITURE|Un empoisonnement par un aliment.|intoxication(s)? alimentaire(s)?;salmonelle(s)?;listeria;empoisonnement(s)? alimentaire(s)?;botulisme
croupi|#DISGUST #NOURRITURE|Corrompu par la stagnation.|croupi(e|s|es)?;croupissant(e|s|es)?;crouppir;eau(x)? stagnante(s)?;eau(x)? croupie(s)?
pourrissement|#DISGUST #NOURRITURE|Un cheveu trouvé dans un plat.|pourrissement(s)?;cheveu(x)? dans (mon|le|ton|ce) (plat|assiette|soupe);poil(s)? dans (mon|le|la) (plat|assiette|soupe)|insecte(s)? dans (mon|le|la) (plat|assiette|salade)|ver(s)? dans (le|la|mon|ma) (fruit|pomme|salade)
graillon|#DISGUST #NOURRITURE|L'odeur de graisse brûlée.|graillon(s)?;odeur(s)? de friture;huile rance;huile de friture usagée;friture graisseuse
abats|#DISGUST #NOURRITURE|Les parties internes des animaux.|abats;tripes;boyaux;entrailles;viscères
gélatine|#DISGUST #NOURRITURE|Une substance molle et tremblante.|gélatine(s)?;gélatineu(x|se|ses);flasque(s)?;spongieu(x|se|ses);mollasson(ne|s|nes)?
corruption|#DISGUST #MORAL|Le fait d'acheter ou de vendre des faveurs.|corruption(s)?;corrompu(e|s|es)?;corrompre;pot(s)?-de-vin;dessous-de-table
hypocrisie|#DISGUST #MORAL|Le fait de feindre des sentiments.|hypocrisie(s)?;hypocrite(s)?;faux-cul(s)?;double discours;tartuferie(s)?
lâcheté|#DISGUST #MORAL|Le manque de courage.|lâcheté(s)?;lâche(s)?;couard(e|s|es)?;dégonflé(e|s|es)?;poltron(ne|s|nes)?
perversion|#DISGUST #MORAL|La déviation vers le mal.|perversion(s)?;pervers(e|es)?;perversité(s)?;vicieu(x|se|ses);détraqué(e|s|es)?
abus|#DISGUST #MORAL|L'usage injuste de son pouvoir.|abus;abus de pouvoir;abus de faiblesse;abus(er|é|ée) de;emprise(s)?
injustice|#DISGUST #MORAL|Le manque de justice.|injustice(s)?;injuste(s)?;iniquité(s)?;deux poids deux mesures;passe-droit(s)?
cruauté|#DISGUST #MORAL|Le plaisir de faire souffrir.|cruauté(s)?;cruel(le|s|les)?;sadisme(s)?;sadique(s)?;maltraitance(s)?
bassesse|#DISGUST #MORAL|Un manque de noblesse morale.|bassesse(s)?;mesquinerie(s)?;mesquin(e|s|es)?;petitesse(s)?;coup(s)? bas
cupidité|#DISGUST #MORAL|Le désir excessif de richesse.|cupidité(s)?;cupide(s)?;avidité(s)?;rapace(s)?;vénal(e|es|aux|ité)?
magouille|#DISGUST #MORAL|Une manœuvre malhonnête.|magouille(s)?;magouill(er|eur|eurs|euse)|combine(s)?;tripatouillage(s)?;manigance(s)?
fraude|#DISGUST #MORAL|Une tromperie pour obtenir un avantage.|fraude(s)?;fraud(er|eur|eurs|euse|uleux|uleuse)|triche(r|rie|ur|use)?;malversation(s)?;détournement(s)? de fonds
népotisme|#DISGUST #MORAL|Le favoritisme envers ses proches.|népotisme(s)?;favoritisme(s)?;copinage(s)?;piston(s)?;emploi(s)? fictif(s)?
racisme|#DISGUST #MORAL|La haine fondée sur l'origine.|racisme(s)?;raciste(s)?;xénophobie(s)?;xénophobe(s)?;propos racistes
sexisme|#DISGUST #MORAL|La discrimination selon le sexe.|sexisme(s)?;sexiste(s)?;misogynie(s)?;misogyne(s)?;machisme(s)?
homophobie|#DISGUST #MORAL|L'hostilité envers les personnes homosexuelles.|homophobie(s)?;homophobe(s)?;transphobie(s)?;transphobe(s)?;lgbtphobie(s)?
antisémitisme|#DISGUST #MORAL|La haine des Juifs.|antisémitisme(s)?;antisémite(s)?;haine antisémite;négationnisme(s)?;négationniste(s)?
discrimination|#DISGUST #MORAL|Le traitement inégal et injuste.|discrimination(s)?;discriminatoire(s)?;discrimin(er|é|ée|és)|ségrégation(s)?;exclusion(s)?
intolérance|#DISGUST #MORAL|Le refus des différences.|intolérance(s)?;intolérant(e|s|es)?;sectarisme(s)?;bigoterie(s)?;étroitesse d'esprit
mépris|#DISGUST #MORAL|Le sentiment qu'autrui ne vaut rien.|mépris;méprisant(e|s|es)?;dédain(s)?;dédaigneu(x|se|ses);condescendance(s)?
arrogance|#DISGUST #MORAL|Une fierté hautaine et blessante.|arrogance(s)?;arrogant(e|s|es)?;suffisance(s)?;prétentieu(x|se|ses);morgue
cynisme|#DISGUST #MORAL|Le mépris des valeurs morales.|cynisme(s)?;cynique(s)?;sans scrupule(s)?;sans vergogne;sans foi ni loi
scandale|#DISGUST #MORAL|Un fait qui choque la conscience.|scandale(s)?;scandaleu(x|se|ses|sement);scandalis(é|ée|és|er)|honte nationale;affaire(s)? sordide(s)?
déshonneur|#DISGUST #MORAL|La perte de l'honneur.|déshonneur(s)?;déshonor(é|ée|ant|ante|er)|opprobre(s)?;flétri(e|s|es)?;avili(e|s|es|ssant|ssante)?
ignominie|#DISGUST #MORAL|Un déshonneur extrême.|ignominie(s)?;turpitude(s)?;vilenie(s)?;indignité(s)?;forfaiture(s)?
dépravation|#DISGUST #MORAL|La corruption des mœurs.|dépravation(s)?;dépravé(e|s|es)?;débauche(s)?;luxure(s)?;stupre(s)?
vice|#DISGUST #MORAL|Un penchant pour le mal.|vice(s)?;immoralité(s)?;immoral(e|es|aux)?;amoral(e|es|aux)?;corrompu(e|s|es)? jusqu'à l'os
mensonge|#DISGUST #MORAL|La parole contraire à la vérité.|menteu(r|rs|se|ses) (patenté|invétéré)(e|s|es)?;mensonge(s)? éhonté(s)?;mytho(mane|manes)?;mensonge(s)?;baratin(s)?
trahison|#DISGUST #MORAL|Le fait de trahir la confiance.|trahison(s)?;traître(s|sse|sses)?;coup de poignard dans le dos;félon(ne|s|nes)?;judas
exploiteur|#DISGUST #MORAL|L'usage abusif du travail d'autrui.|exploitation(s)? (des|de la) (travailleurs|misère|enfants)|esclavage(s)? moderne(s)?;travail des enfants;exploiteur(s)?;négrier(s)?
pédophilie|#DISGUST #MORAL #CRIME|Les abus sexuels sur enfants.|pédophilie(s)?;pédophile(s)?;pédocriminalité(s)?;pédocriminel(s)?;abus sexuel(s)? sur mineur(s)?
inceste|#DISGUST #MORAL #CRIME|Les relations sexuelles entre proches parents.|inceste(s)?;incestueu(x|se|ses);violence(s)? intrafamiliale(s)?
harceleur|#DISGUST #MORAL|Des agissements répétés qui humilient.|harcèlement(s)? sexuel(s)?;harceleur(s)?;main(s)? aux fesses;gros lourd;comportement(s)? déplacé(s)?
maltraitance|#DISGUST #MORAL|Les mauvais traitements.|maltraitance(s)?;maltrait(er|é|ée|és|ées)|enfant(s)? battu(s)?;animaux maltraités;violence(s)? conjugale(s)?
torture|#DISGUST #MORAL|Les souffrances infligées à dessein.|torture(s)?;tortionnaire(s)?;sévices;barbarie(s)?;supplice(s)? infligé(s)?
sadisme|#DISGUST #MORAL|Le plaisir de faire souffrir.|sadisme(s)?;sadisme(s)? pur(s)?;sadique(s)? en puissance;jouissance de la souffrance;plaisir de faire souffrir;cruauté gratuite
fourbe|#DISGUST #MORAL|Qui feint ce qu'il n'est pas.|hypocrite(s)? de première;faux jeton(s)?;langue de vipère;sainte nitouche;fourbe(s)?
escroc|#DISGUST #MORAL|Une personne qui trompe pour voler.|escroc(s)?;escroquerie(s)?;arnaqueu(r|rs|se|ses);filou(s)?;truand(s)?
voyou|#DISGUST #MORAL|Une personne malhonnête et sans morale.|voyou(s)?;crapule(s)?;racaille(s)?;canaille(s)?;malfrat(s)?
ordurier|#DISGUST #MORAL|Grossier jusqu'à l'obscénité.|ordurier(s)?;ordurière(s)?;propos orduriers;insulte(s)? ordurière(s)?;langage de charretier
salaud|#DISGUST #MORAL|Une personne méprisable (familier).|salaud(s)?;salope(rie|ries)?;fumier;pourriture;enfoiré(e|s|es)?
rebut|#DISGUST #MORAL|Une personne traitée comme un déchet (injure).|rebut(s)?;rebut de l'humanité;ordure(s)? humaine(s)?;vermine humaine;moins que rien;sous-homme(s)?
lèche-bottes|#DISGUST #MORAL|Une personne servile et flatteuse.|lèche-bottes;lèche-cul(s)?;fayot(s)?;flagorneur(s)?;carpette(s)?
sournois|#DISGUST #MORAL|Qui cache ses mauvaises intentions.|sournois(e|es)?;hypocritement;perfide(s)?;faux(-)?jeton(s)?;mielleu(x|se|ses)
gaspillage|#DISGUST #MORAL|La dépense inutile, le gâchis.|gaspillage(s)?;gâchis;gaspill(er|é|ée)|gabegie(s)?;dilapid(er|é|ée|ation)
pollueur|#DISGUST #MORAL #INSALUBRITE|Celui qui salit l'environnement.|pollueur(s)?;écocide(s)?;greenwashing;déversement(s)? toxique(s)?;dégazage(s)? sauvage(s)?
dégradant|#DISGUST #MORAL|Qui avilit, humilie.|dégradant(e|s|es)?;avilissant(e|s|es)?;humiliant(e|s|es)?;rabaissant(e|s|es)?;déshumanisant(e|s|es)?
honteux|#DISGUST #MORAL|Qui devrait faire honte.|honteu(x|se|ses|sement);c'est une honte;quelle honte;honte à (lui|elle|eux|vous);inadmissible(s)?
inadmissible|#DISGUST #MORAL|Qu'on ne peut accepter.|inadmissible(s)?;inacceptable(s)?;intolérable(s)?;impardonnable(s)?;indéfendable(s)?
indignation|#DISGUST #MORAL|La colère devant l'injustice.|indignation(s)?;indign(é|ée|és|ées|er|ant|ante)|outr(é|ée|age|ageant)|choqué(e|s|es)?;révolté(e|s|es)?
écœuré|#DISGUST #MORAL|Dégoûté au point d'abandonner.|écœuré(e|s|es)?;écœuré(e|s|es)? par;ras-le-bol;dégoûté(e|s|es)? de tout;j'en ai marre;la nausée de tout
écœurant|#DISGUST #MORAL|Moralement révoltant.|écœurant(e|s|es)?;moralement écœurant;c'est écœurant;comportement(s)? écœurant(s)?;attitude écœurante;spectacle écœurant
vénalité|#DISGUST #MORAL|Le fait de se vendre pour de l'argent.|vénalité(s)?;vendu(e|s|es)? (au plus offrant|aux lobbies)|mercantile(s)?;âpreté au gain;tout pour le fric
lobbying|#DISGUST #MORAL|L'influence d'intérêts privés sur les décisions.|lobbying;lobby(s|ies)?;conflit(s)? d'intérêts;pantouflage(s)?;trafic d'influence
démagogie|#DISGUST #MORAL|La démagogie qui flatte les instincts.|démagogie(s)?;démagogique(s)?;démago(s)?;populisme(s)? (de bas étage|crasse);racolage(s)?
intox|#DISGUST #MORAL|La manipulation de l'opinion.|propagande(s)? (abjecte|nauséabonde)(s)?;bourrage de crâne;intox;discours nauséabond(s)?;relent(s)? (raciste|fasciste)(s)?
fascisme|#DISGUST #MORAL|Une idéologie totalitaire et violente.|fascisme(s)?;fasciste(s)?;nazi(e|s|es|sme)?;néonazi(e|s|es)?;suprémaciste(s)?
barbare|#DISGUST #MORAL|D'une cruauté sauvage.|barbare(s)?;bestial(e|es|aux|ité)?;sauvage(s)? et cruel(s)?;inhumanité(s)?;monstre(s)? humain(s)?
exhibitionniste|#DISGUST #MORAL|Qui fait le mal par plaisir.|pervers narcissique(s)?;manipulateur(s)? pervers;prédateur(s)? sexuel(s)?;exhibitionniste(s)?;voyeur(s)?
viol|#DISGUST #MORAL #CRIME|Une agression sexuelle.|viol(s)?;violeur(s)?;agression(s)? sexuelle(s)?;attouchement(s)?;culture du viol
souiller|#DISGUST #MORAL|Salir moralement.|souiller;souill(e|ent|ait|ure morale)|salir (la|sa|leur) mémoire;entacher;éclaboussé(e|s|es)? par le scandale
trahir|#DISGUST #MORAL|Manquer à la confiance.|trah(ir|i|ie|is|ies|it|issent|issait)|poignard(er|é|ée) dans le dos|vendre (ses|son) ami(s)?;retourner sa veste;double jeu
glaire|#DISGUST #CORPS|Un mucus épais.|glaire(s)?;glaireu(x|se|ses);mucosité(s)? épaisse(s)?;sécrétion(s)?;expectoration(s)?
moite|#DISGUST #CORPS|Humide de sueur.|moite(s)?;mains moites;moiteur(s)?;suant(e|s|es)?;dégoulinant(e|s|es)? de sueur
fiente|#DISGUST #SALETE|Les excréments d'oiseaux.|fiente(s)?;fiente(s)? de pigeon;guano;chiure(s)?;crotte(s)? de chien
lèpre|#DISGUST #CORPS|Une maladie qui ronge la peau.|lèpre(s)?;lépreu(x|se|ses);gale(s)?;galeu(x|se|ses);teigne(s)?
mycose|#DISGUST #CORPS|Une infection par des champignons.|mycose(s)?;champignon(s)? (aux pieds|des ongles);pied(s)? d'athlète;ongle(s)? jaune(s)?;odeur(s)? de pieds
araignée|#DISGUST #VERMINE|Un animal à huit pattes qui tisse des toiles.|araignée(s)?;toile(s)? d'araignée;tarentule(s)?;mygale(s)?;arachnide(s)?
lombric|#DISGUST #VERMINE|Le ver de terre.|lombric(s)?;annélide(s)?;ver(s)? gluant(s)?;ver(s)? de vase;tortillant(e|s|es)?
moustique|#DISGUST #VERMINE|Un insecte piqueur.|moustique(s)?;piqûre(s)? de moustique;nuée(s)? de moustiques;moucheron(s)?;taon(s)?
crapaud|#DISGUST #VERMINE|Un batracien à la peau pustuleuse.|crapaud(s)?;pustuleu(x|se|ses);bave de crapaud;batracien(s)? gluant(s)?;peau verruqueuse
caillé|#DISGUST #NOURRITURE|Coagulé, tourné.|caillé(e|s|es)?;lait caillé;grumeleu(x|se|ses);grumeau(x)?;dépôt(s)? douteu(x|se)
douteux|#DISGUST #NOURRITURE|D'une fraîcheur incertaine.|douteu(x|se|ses);pas frais;pas fraîche;suspect(e|s|es)? (au goût|à l'odeur);louche(s)?
servile|#DISGUST #MORAL|Qui rampe devant les puissants.|servile(s)?;servilité(s)?;obséquieu(x|se|ses);courbette(s)?;rampant(e|s|es)? devant
délation|#DISGUST #MORAL|La dénonciation intéressée.|délation(s)?;délateur(s)?;délatrice(s)?;mouchard(e|s|es)?;dénonciation(s)? calomnieuse(s)?
calomnie|#DISGUST #MORAL|Une accusation mensongère.|calomnie(s)?;calomni(er|é|ée|ateur|atrice|eux|euse)|diffamation(s)?;diffam(er|é|ée|atoire)|ragot(s)?
ingratitude|#DISGUST #MORAL|Le manque de reconnaissance.|ingratitude(s)?;ingrat(e|s|es)?;cracher dans la soupe;mordre la main qui (te|vous|le|la) nourrit;sans un merci
égoïsme|#DISGUST #MORAL|Le fait de ne penser qu'à soi.|égoïsme(s)?;égoïste(s)?;égocentrique(s)?;nombriliste(s)?;individualisme(s)? forcené(s)?
goinfre|#DISGUST|Qui mange avec avidité et sans manières.|goinfre(s)?;goinfrerie(s)?;s'empiffr(er|e|ent)|bâfr(er|e|ent)|glouton(ne|s|nes)?
ivrogne|#DISGUST|Une personne souvent ivre.|ivrogne(s)?;ivrognerie(s)?;soûlard(e|s|es)?;poivrot(e|s|es)?;ivre mort(e|s|es)?
obsédé|#DISGUST #MORAL|Une personne aux pensées sexuelles envahissantes.|obsédé(e|s|es)?;obsédé(e|s|es)? sexuel(le|s|les)?;lubrique(s)?;libidineu(x|se|ses);vieux cochon(s)?
proxénétisme|#DISGUST #MORAL #CRIME|L'exploitation de la prostitution d'autrui.|proxénétisme(s)?;proxénète(s)?;exploitation sexuelle;traite des femmes;réseau(x)? de prostitution
trafiquant|#DISGUST #MORAL #CRIME|Celui qui fait un commerce illégal.|trafiquant(e|s|es)?;trafic(s)? d'organes;trafic(s)? d'êtres humains;traite humaine;passeur(s)? sans scrupules
braconnage|#DISGUST #MORAL|La chasse illégale.|braconnage(s)?;braconnier(s)?;massacre d'animaux;chasse au trophée;ivoire illégal
corrida|#DISGUST #MORAL|La mise à mort d'un taureau en spectacle.|corrida(s)?;tauromachie(s)?;mise à mort du taureau;combat(s)? de coqs;souffrance animale
abattoir|#DISGUST #MORAL|Le lieu où l'on tue les animaux.|abattoir(s)?;élevage(s)? intensif(s)?;poules en batterie;usine(s)? à viande;souffrance des animaux
complaisance|#DISGUST #MORAL|Une indulgence coupable.|complaisance(s)?;complaisant(e|s|es)?;laxisme(s)?;laxiste(s)?;fermer les yeux
impunité|#DISGUST #MORAL|Le fait de ne pas être puni.|impunité(s)?;impuni(e|s|es)?;s'en tirer sans rien;au-dessus des lois;justice à deux vitesses
indécent|#DISGUST #MORAL|Qui choque par son excès.|indécent(e|s|es)?;salaire(s)? indécent(s)?;richesse(s)? indécente(s)?;étalage de luxe;ostentatoire(s)?
profiteur|#DISGUST #MORAL|Celui qui profite des autres.|profiteu(r|rs|se|ses);parasite(s)? sociaux;resquilleu(r|rs|se|ses);pique-assiette(s)?;sangsue(s)? (de la société|du système)
tricheur|#DISGUST #MORAL|Celui qui triche.|tricheu(r|rs|se|ses);tricherie(s)?;dopage(s)?;dopé(e|s|es)?;match(s|es)? truqué(s)?
plaie|#DISGUST #CORPS|Une blessure ouverte.|plaie(s)?;plaie(s)? béante(s)?;chair à vif;blessure(s)? suintante(s)?;sanguinolent(e|s|es)?
bactérie|#DISGUST #INSALUBRITE|Un micro-organisme, souvent porteur de maladie.|bactérie(s)?;microbe(s)?;germe(s)?;nid(s)? à microbes;bouillon de culture
crasseux|#DISGUST #SALETE|Couvert de crasse.|crasseu(x|se|ses);mal lavé(e|s|es)?;jamais lavé(e|s|es)?;sent le fauve;pue des pieds
souillon|#DISGUST #SALETE|Une personne malpropre.|souillon(s)?;salit tout;mange comme un porc;vit dans une porcherie;mal tenu(e|s|es)?
vermoulu|#DISGUST #SALETE|Rongé, abîmé par le temps.|vermoulu(e|s|es)?;rongé(e|s|es)? par les vers;miteu(x|se|ses);mité(e|s|es)?;décrépit(e|s|es)?
scatologique|#DISGUST #CORPS|Qui parle des excréments.|scatologique(s)?;scato;humour pipi-caca;blague(s)? scatologique(s)?;scatologie(s)?
bourreau|#DISGUST #MORAL|Celui qui fait souffrir.|bourreau(x)?;tortionnaire(s)?;persécuteur(s)?;tyran(s)? domestique(s)?;harceleur(s)?
chantage|#DISGUST #MORAL|La menace pour obtenir quelque chose.|chantage(s)?;maître(s)?-chanteur(s)?;extorsion(s)?;racket(s)?;rançonn(er|é|ée)
délinquant|#DISGUST #MORAL|Celui qui commet des délits.|délinquant(e|s|es)?;délinquance(s)?;malfaiteur(s)?;voleur(s)?;cambrioleur(s)?
mazout|#DISGUST #INSALUBRITE|Le pétrole qui souille les côtes.|mazout(s)?;mazouté(e|s|es)?;galette(s)? de goudron;nappe(s)? de pétrole;pétrole sur la plage
fosse|#DISGUST #INSALUBRITE|Une cavité où s'accumulent les matières sales.|fosse(s)? septique(s)?;fosse(s)? à purin;fosse(s)?;vidange(s)? de fosse;boues d'épuration
machiavélique|#DISGUST #MORAL|D'une ruse perfide.|machiavélique(s)?;manipulat(eur|rice|eurs|rices);intrigant(e|s|es)?;comploteu(r|rs|se|ses);calculateu(r|rs|se|ses)
vautour|#DISGUST #MORAL|Celui qui profite du malheur des autres.|vautour(s)?;fonds vautour(s)?;charognard(s)? de la finance;profiter du malheur;rapace(s)? sans pitié
sanguinaire|#DISGUST #MORAL|Qui aime faire couler le sang.|sanguinaire(s)?;assoiffé(e|s|es)? de sang;impitoyable(s)?;féroce(s)?;boucher(s)? sanguinaire(s)?
"""

if __name__ == "__main__":
    seedpatterns.write(DATA, "fr-disgust", "!DISGUST", 200, pathlib.Path(__file__).with_name("symbolicpatterns-fr-degout.json"))
