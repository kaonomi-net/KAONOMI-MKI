"""
Generates symbolicpatterns-fr-negatifs.json: 97 symbolic patterns (PUT /symbolicpattern) for French negative and
morbid words (fear itself, angoisse and terreur are in the !FEAR patterns). Each entry: the symbol is the first word; the regexes match that word, its inflections and up to five
synonyms (case ignored, whole words, accents optional: "deces" matches as "décès"); the signal !SAD, no event; the
title, tags and comment follow the meaning of the words; language FR. The ids are stable (fr-sad-<symbol>): sending
the file again overwrites the same documents.

Usage: python make_symbolicpatterns_fr_negatifs.py  (writes the JSON next to this script)
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import seedpatterns  # noqa: E402

# symbol | tags | comment | regex bodies (the head word first, then its synonyms), separated by ';'
# In the bodies, an accented letter also matches its plain form (é -> [ée]).
DATA = """
mort|#MORT #DEUIL|La mort, la fin de la vie.|mort(e|s|es)?;décès;trépas;défunt(e|s|es)?;perte(s)? humaine(s)?
mourir|#MORT|L'action de mourir, de perdre la vie.|mour(ir|ant|ante|ants|antes|ra|rait|rons|ront)|meur(s|t|ent)|décéd(er|é|ée|és|ées|ait)|périr|péri(e|s|es|ssent|t)?|succomb(er|é|ée|ait|e|ent)|expir(er|é|ée|ait)
décès|#MORT #DEUIL|Le décès, la disparition d'une personne.|décès;disparition(s)?;perte(s)? (d'un|d'une) proche(s)?;mortalité(s)?
tuer|#MORT #VIOLENCE|Donner la mort à un être vivant.|tu(er|e|es|ent|é|ée|és|ées|ait|aient|era|erai|eur|eurs|euse)|abatt(re|u|ue|us|ues|ait)|exécut(er|é|ée|és)|éliminer|élimin(é|ée|és|ées)
meurtre|#MORT #VIOLENCE #CRIME|Le fait de tuer volontairement quelqu'un.|meurtre(s)?;meurtri(er|ère|ers|ères);homicide(s)?;crime(s)? de sang
assassinat|#MORT #VIOLENCE #CRIME|Meurtre prémédité.|assassinat(s)?;assassin(e|s|es)?;assassin(er|é|ée|és|ées|ait);tueur(s)? à gages
suicide|#MORT #DETRESSE|Le fait de se donner la mort.|suicide(s)?;suicid(er|é|ée|és|ées|aire|aires);(mettre|met|mets|mettent|mettait|mis|mise) fin à (ses|mes|tes|leurs|nos|vos) jours;(se|me|te) donner la mort;autolyse
cadavre|#MORT|Le corps d'une personne morte.|cadavre(s)?;dépouille(s)?;macchabée(s)?;corps sans vie;charogne(s)?
tombe|#MORT #DEUIL|Le lieu où l'on enterre un mort.|tombe(s)?;tombeau(x)?;sépulture(s)?;caveau(x)?;mausolée(s)?
cercueil|#MORT #DEUIL|La caisse où l'on place le corps d'un mort.|cercueil(s)?;bière(s)? funéraire(s)?;sarcophage(s)?;urne(s)? funéraire(s)?
enterrement|#MORT #DEUIL|La mise en terre d'un mort.|enterrement(s)?;enterr(er|é|ée|és|ées);inhumation(s)?;inhum(er|é|ée)|ensevel(ir|i|ie|is|ies)
funérailles|#MORT #DEUIL|La cérémonie qui accompagne un mort.|funérailles;obsèques;funéraire(s)?;cortège(s)? funèbre(s)?;crémation(s)?
deuil|#DEUIL #TRISTESSE|La douleur et le temps qui suivent la mort d'un proche.|deuil(s)?;endeuill(er|é|ée|és|ées);condoléances;pleurer (un|une|son|sa) (mort|morte|défunt|défunte)
veuf|#DEUIL|La personne dont le conjoint est mort.|veu(f|fs|ve|ves);veuvage(s)?;orphelin(e|s|es)?;orphelinat(s)?
cimetière|#MORT #DEUIL|Le lieu où l'on enterre les morts.|cimetière(s)?;nécropole(s)?;ossuaire(s)?;catacombe(s)?;columbarium(s)?
agonie|#MORT #SOUFFRANCE|Les derniers moments avant la mort.|agonie(s)?;agonis(er|ant|ante|ants|ait|e);râle(s)?;dernier(s)? souffle(s)?;fin de vie
tristesse|#TRISTESSE|L'état de celui qui est triste.|tristesse(s)?;triste(s)?;(?<!à )peine(s)?;affligé(e|s|es)?;abattu(e|s|es)?
chagrin|#TRISTESSE|Une peine morale profonde.|chagrin(s)?;chagrin(é|ée|és|ées);affliction(s)?;navré(e|s|es)?;crève-cœur
désespoir|#TRISTESSE #DETRESSE|La perte de tout espoir.|désespoir(s)?;désespér(é|ée|és|ées|er|ant|ante);sans espoir;découragé(e|s|es)?;accablé(e|s|es)?
dépression|#TRISTESSE #DETRESSE #MALADIE|Un trouble de l'humeur, une profonde tristesse.|dépression(s)?;dépressi(f|fs|ve|ves);déprim(e|é|ée|és|ées|er|ant|ante);neurasthénie;burn-?out
solitude|#TRISTESSE #RELATION|L'état de celui qui est seul.|solitude(s)?;seul(e|s|es)? au monde;isol(é|ée|és|ées|ement);esseulé(e|s|es)?;délaissé(e|s|es)?
abandon|#TRISTESSE #RELATION|Le fait d'être laissé, quitté.|abandon(s)?;abandonn(er|é|ée|és|ées|e|ent);largué(e|s|es)?;rejet(é|ée|és|ées)?;lâché(e|s|es)?
douleur|#SOUFFRANCE|Une sensation pénible, physique ou morale.|douleur(s)?;douloureu(x|se|ses);mal à (en )?mourir;supplice(s)?;calvaire(s)?
souffrance|#SOUFFRANCE|Le fait de souffrir.|souffrance(s)?;souffr(ir|e|es|ent|ant|ante|ait|u)|endur(er|é|e);martyre(s)?;tourment(s)?
pleurer|#TRISTESSE|Verser des larmes.|pleur(er|e|es|ent|é|ait|aient|ant|eur|eurs)|sanglot(s|er|e|ent)?;larmoy(er|ant|ante)|chialer;fondre en larmes
larme|#TRISTESSE|La goutte qui coule des yeux quand on pleure.|larme(s)?;en larmes;yeux rougis;sanglot(s)?
malheur|#TRISTESSE #CATASTROPHE|Un événement pénible, la mauvaise fortune.|malheur(s)?;malheureu(x|se|ses|sement);infortune(s)?;détresse(s)?;misère(s)? noire(s)?
drame|#CATASTROPHE|Un événement tragique.|drame(s)?;dramatique(s)?;tragique(s)?;funeste(s)?;sinistre(s)?
tragédie|#CATASTROPHE|Un événement terrible, aux suites fatales.|tragédie(s)?;tragiquement;fatal(e|s|es)?;fatalité(s)?
catastrophe|#CATASTROPHE|Un désastre, un malheur soudain et grand.|catastrophe(s)?;catastrophique(s)?;désastre(s)?;désastreu(x|se|ses);cataclysme(s)?
guerre|#GUERRE #VIOLENCE|Un conflit armé.|guerre(s)?;guerri(er|ère|ers);conflit(s)? armé(s)?;bombardement(s)?;bombard(er|é|ée|és)
massacre|#GUERRE #VIOLENCE #MORT|La tuerie d'un grand nombre de personnes.|massacre(s)?;massacr(er|é|ée|és|ées);tuerie(s)?;carnage(s)?;boucherie(s)?
génocide|#GUERRE #VIOLENCE #MORT|L'extermination d'un peuple.|génocide(s)?;génocidaire(s)?;extermination(s)?;extermin(er|é|ée|és);épuration(s)? ethnique(s)?
attentat|#VIOLENCE #TERRORISME|Une attaque criminelle contre des personnes.|attentat(s)?;attentat-suicide;kamikaze(s)?;voiture(s)? piégée(s)?;prise(s)? d'otages?
terrorisme|#VIOLENCE #TERRORISME|L'emploi de la terreur à des fins politiques.|terrorisme(s)?;terroriste(s)?;djihadiste(s)?;radicalis(é|ée|és|ation)
violence|#VIOLENCE|L'usage de la force contre quelqu'un.|violence(s)?;violent(e|s|es)?;brutalité(s)?;brutal(e|es|aux)?;frapp(er|é|ée|és|ées)
agression|#VIOLENCE #CRIME|Une attaque violente contre quelqu'un.|agression(s)?;agress(er|é|ée|és|ées|eur|eurs);tabass(er|é|ée|age);passage(s)? à tabac;lynchage(s)?
torture|#VIOLENCE #SOUFFRANCE|Les souffrances infligées volontairement.|torture(s)?;tortur(er|é|ée|és|ées|eur|eurs);sévices;supplicié(e|s|es)?;martyris(er|é|ée)
haine|#ANGER #VIOLENCE|Une hostilité violente envers quelqu'un.|haine(s)?;haïr;hai(s|t|ssent)|haï(e|s|es)?;détest(er|e|es|ent|é|ée);exécr(er|é|able)
colère|#ANGER|Une vive irritation.|colère(s)?;furieu(x|se|ses);rage(s)?;enrag(é|ée|és|ées);fureur(s)?
cauchemar|#FEAR|Un rêve effrayant ; une situation pénible.|cauchemar(s)?;cauchemardesque(s)?;mauvais rêve(s)?;terreur(s)? nocturne(s)?
maladie|#MALADIE|Une altération de la santé.|maladie(s)?;malade(s)?;pathologie(s)?;affection(s)? grave(s)?;mal incurable
cancer|#MALADIE #MORT|Une maladie grave due à une tumeur maligne.|cancer(s)?;cancéreu(x|se|ses);tumeur(s)?;métastase(s)?;leucémie(s)?
épidémie|#MALADIE #CATASTROPHE|La propagation rapide d'une maladie.|épidémie(s)?;pandémie(s)?;peste(s)?;choléra;contagion(s)?
blessure|#SOUFFRANCE #VIOLENCE|Une lésion du corps.|blessure(s)?;bless(é|ée|és|ées|er);plaie(s)?;mutil(é|ée|és|ées|ation);traumatisme(s)?
accident|#CATASTROPHE|Un événement soudain et dommageable.|accident(s)?;accidenté(e|s|es)?;collision(s)?;carambolage(s)?;crash(s|es)?
noyade|#MORT #CATASTROPHE|La mort par immersion.|noyade(s)?;noy(er|é|ée|és|ées|ait);naufrage(s)?;naufragé(e|s|es)?;englouti(e|s|es)?
incendie|#CATASTROPHE|Un grand feu destructeur.|incendie(s)?;incendi(er|é|ée|aire|aires);brasier(s)?;calciné(e|s|es)?;brûlé(e|s|es)? vif(s|ve|ves)?
explosion|#CATASTROPHE #VIOLENCE|Une déflagration violente.|explosion(s)?;explos(er|é|ée|if|ifs|ive|ives);déflagration(s)?;détonation(s)?;bombe(s)?
effondrement|#CATASTROPHE|La chute brutale de ce qui tenait debout.|effondrement(s)?;effondr(er|é|ée|és|ées);écroul(ement|é|ée|er);éboulement(s)?;séisme(s)?
ruine|#ECHEC #MISERE|La perte de ses biens ; un bâtiment détruit.|ruine(s)?;ruin(er|é|ée|és|ées);décombres;dévast(er|é|ée|ation);ravag(er|é|ée|és)
faillite|#ECHEC #MISERE|L'état d'une entreprise qui ne peut plus payer.|faillite(s)?;banqueroute(s)?;liquidation(s)? judiciaire(s)?;dépôt de bilan;insolvab(le|les|ilité)
misère|#MISERE|Un extrême dénuement.|misère(s)?;misérable(s)?;dénuement(s)?;indigence(s)?;précarité(s)?
pauvreté|#MISERE|Le manque d'argent, de ressources.|pauvreté(s)?;pauvre(s)?;démuni(e|s|es)?;sans-abri;sdf
famine|#MISERE #MORT|Le manque total de nourriture d'une population.|famine(s)?;affam(é|ée|és|ées|er);disette(s)?;malnutrition(s)?;mourir de faim
échec|#ECHEC|Le fait de ne pas réussir.|échec(s)?;échou(er|é|ée|e|ent|ait);fiasco(s)?;revers;débâcle(s)?
perte|#DEUIL #ECHEC|Le fait de perdre quelqu'un ou quelque chose.|perte(s)?;perdu(e|s|es)?;privation(s)?;manque(s)? cruel(s)?;irréparable(s)?
regret|#TRISTESSE|Le chagrin de ce qui est passé ou perdu.|regret(s)?;regrett(er|e|es|ent|é|ée|able);remords;repentir;nostalgie(s)?
culpabilité|#TRISTESSE #DETRESSE|Le sentiment d'être coupable.|culpabilité(s)?;coupable(s)?;culpabilis(er|é|ée|ant|ante);faute(s)? grave(s)?
honte|#TRISTESSE|Un sentiment d'humiliation, de déshonneur.|honte(s)?;honteu(x|se|ses|sement);déshonneur(s)?;opprobre;ignominie(s)?
humiliation|#TRISTESSE #RELATION|Le fait d'être rabaissé.|humiliation(s)?;humili(er|é|ée|és|ées|ant|ante);rabaiss(er|é|ée);mépris(é|ée|és)?;affront(s)?
trahison|#RELATION|Le fait de trahir.|trahison(s)?;trah(ir|i|ie|is|ies|it|issent);traître(s|sse|sses)?;félonie(s)?;perfidie(s)?
mensonge|#RELATION|Une affirmation contraire à la vérité.|mensonge(s)?;menteu(r|rs|se|ses);ment(ir|i|ait|ent);tromperie(s)?;duperie(s)?
rupture|#RELATION #TRISTESSE|La fin d'une relation.|rupture(s)?;rompu(e|s|es)?;quitté(e|s|es)?;chagrin(s)? d'amour;cœur brisé
divorce|#RELATION|La rupture légale d'un mariage.|divorce(s)?;divorc(er|é|ée|és|ées);séparé(e|s|es)?;garde des enfants
séparation|#RELATION #TRISTESSE|Le fait d'être éloigné de quelqu'un.|séparation(s)?;sépar(er|é|ée|és|ées);éloignement(s)?;déchirement(s)?
exil|#TRISTESSE #MISERE|Le fait d'être chassé de son pays.|exil(s)?;exil(é|ée|és|ées|er);déraciné(e|s|es)?;réfugié(e|s|es)?;banni(e|s|es)?
prison|#JUSTICE|Le lieu où l'on enferme les condamnés.|prison(s)?;prisonni(er|ère|ers|ères);incarcér(é|ée|és|ation|er);détention(s)?;cachot(s)?
condamnation|#JUSTICE|La décision qui inflige une peine.|condamnation(s)?;condamn(er|é|ée|és|ées);peine(s)? de prison;perpétuité;réclusion(s)?
exécution|#JUSTICE #MORT|La mise à mort d'un condamné.|exécution(s)?;peine(s)? de mort;pendaison(s)?;guillotine(s)?;peloton(s)? d'exécution
sang|#VIOLENCE #MORT|Le liquide rouge des veines ; la violence qui le fait couler.|sang(s)?;sanglant(e|s|es)?;ensanglant(é|ée|és|ées);hémorragie(s)?;saign(er|e|é|ée|ant)
poison|#MORT #CRIME|Une substance qui tue ou rend malade.|poison(s)?;empoisonn(er|é|ée|és|ement);venin(s)?;toxique(s)?;intoxication(s)?
squelette|#MORT #SURNATUREL|Les os d'un mort.|squelette(s)?;ossement(s)?;crâne(s)?;tête(s)? de mort;macabre(s)?
fantôme|#SURNATUREL #MORT|L'apparition d'un mort.|fantôme(s)?;spectre(s)?;revenant(s)?;esprit(s)? frappeur(s)?;hant(é|ée|és|ées|er)
enfer|#SURNATUREL|Le lieu de la damnation ; une situation insupportable.|enfer(s)?;infernal(e|es|aux)?;damn(é|ée|és|ées|ation);géhenne
démon|#SURNATUREL|Un être malfaisant.|démon(s)?;démoniaque(s)?;diable(s)?;diabolique(s)?;satan(ique|iques)?
malédiction|#SURNATUREL #MALHEUR|Un sort funeste.|malédiction(s)?;maudi(t|te|ts|tes|re);sort(s)? funeste(s)?;mauvais œil;porte-malheur
apocalypse|#CATASTROPHE #SURNATUREL|La fin du monde.|apocalypse(s)?;apocalyptique(s)?;fin du monde;armageddon;chaos
néant|#DETRESSE|Le rien, l'absence de tout.|néant(s)?;anéant(ir|i|ie|is|ies|issement);annihil(er|é|ée|ation)
lamentation|#TRISTESSE|La plainte de celui qui souffre.|lamentation(s)?;lament(er|e|ent|é|able|ables);gémi(r|t|ssent|ssement|ssements);plainte(s)?;jérémiade(s)?
ténèbres|#SURNATUREL #FEAR|L'obscurité profonde.|ténèbres;ténébreu(x|se|ses);obscurité(s)?;nuit noire;sombre(s)?
détresse|#DETRESSE|Un sentiment d'abandon et d'impuissance.|détresse(s)?;désarroi(s)?;au bout du rouleau;à bout;impuissance(s)?
désolation|#TRISTESSE #CATASTROPHE|Une peine extrême ; un lieu ravagé et vide.|désolation(s)?;désol(é|ée|és|ées|ant|ante);navrant(e|s|es)?;lugubre(s)?;affligeant(e|s|es)?
mélancolie|#TRISTESSE|Une tristesse vague et durable.|mélancolie(s)?;mélancolique(s)?;spleen(s)?;morosité(s)?;morose(s)?
épuisement|#DETRESSE #SOUFFRANCE|Une fatigue extrême.|épuisement(s)?;épuis(é|ée|és|ées|ant|ante);exténué(e|s|es)?;harassé(e|s|es)?;à bout de forces
menace|#FEAR #VIOLENCE|L'annonce d'un mal à venir.|menace(s)?;menac(er|é|ée|és|ant|ante|ait);intimidation(s)?;chantage(s)?;représailles
danger|#FEAR|Ce qui menace la sécurité.|danger(s)?;dangereu(x|se|ses);péril(s)?;périlleu(x|se|ses);mortel(le|s|les)?
victime|#VIOLENCE #CRIME|La personne qui subit un mal.|victime(s)?;martyr(e|s|es)?;sinistré(e|s|es)?;rescapé(e|s|es)?;survivant(e|s|es)?
enlèvement|#CRIME #FEAR|Le rapt d'une personne.|enlèvement(s)?;enlev(é|ée|és|ées|er);kidnapping(s)?;kidnapp(er|é|ée|eur|eurs);rapt(s)?
disparition|#FEAR #DEUIL|Le fait de ne plus être retrouvé.|disparition(s)?;disparu(e|s|es)?;introuvable(s)?;porté(e|s|es)? disparu(e|s|es)?
harcèlement|#VIOLENCE #RELATION|Des attaques répétées contre quelqu'un.|harcèlement(s)?;harcel(er|é|ée|és|ées)|harcèle(nt|s)?|persécution(s)?;persécut(er|é|ée|eur|eurs)|cyberharcèlement
détruire|#VIOLENCE #CATASTROPHE|Mettre en ruine, faire disparaître.|détrui(re|t|s|sent|sait)|détruit(e|s|es)?;destruction(s)?;démol(ir|i|ie|ition);saccag(er|é|ée|e)
cendre|#MORT #CATASTROPHE|Ce qui reste de ce qui a brûlé, des morts incinérés.|cendre(s)?;réduit(e|s|es)? en cendres;incinér(é|ée|ation|er);poussière(s)?
linceul|#MORT #DEUIL|Le drap qui enveloppe un mort.|linceul(s)?;suaire(s)?;mortuaire(s)?;chambre(s)? funéraire(s)?;veillée(s)? funèbre(s)?
nécrologie|#MORT #DEUIL|L'annonce ou le récit de la mort d'une personne.|nécrologie(s)?;nécrologique(s)?;avis de décès;faire-part de décès;oraison(s)? funèbre(s)?
euthanasie|#MORT #MALADIE|Le fait de provoquer la mort pour abréger les souffrances.|euthanasie(s)?;euthanasi(er|é|ée);suicide(s)? assisté(s)?;aide(s)? à mourir;soins palliatifs
overdose|#MORT #MALADIE|Une dose mortelle de drogue.|overdose(s)?;surdose(s)?;intoxication(s)? mortelle(s)?;drogue(s)? dure(s)?;toxicomane(s)?
"""

if __name__ == "__main__":
    seedpatterns.write(DATA, "fr-sad", "!SAD", 97, pathlib.Path(__file__).with_name("symbolicpatterns-fr-negatifs.json"))
